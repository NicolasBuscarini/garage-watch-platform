using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using GarageWatch.Application.Analysis;
using GarageWatch.Application.Biometrics;
using GarageWatch.Application.Identity;
using GarageWatch.Infrastructure.Identity.Repositories;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed record BiometricEnrollmentSummary(string PublicId, string UserPublicId, string Status,
    DateTimeOffset EnrolledAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? RevokedAtUtc, string RevokedReason,
    string ModelId, string ModelVersion, int Dimensions, string KeyVersion, string ConsentVersion,
    DateTimeOffset ConsentAcceptedAtUtc);

/// <summary>Enrollment cannot run because no extractor/keys are configured or extraction failed.</summary>
public sealed class BiometricEnrollmentUnavailableException(string code) : Exception(code);

/// <summary>Concurrent enrollment for the same account.</summary>
public sealed class BiometricEnrollmentConflictException(string code) : Exception(code);

/// <summary>The account did not accept the current consent text.</summary>
public sealed class BiometricConsentRequiredException() : Exception("biometric_consent_required");

/// <summary>The submitted image cannot be enrolled (no face, several faces, unreadable file).</summary>
public sealed class BiometricEnrollmentRejectedException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class BiometricEnrollmentService(IamDbContext database, IIamUnitOfWork unitOfWork,
    IIamAuditEventRepository audit, AccessProfileService profiles, IBiometricDataProtector protector,
    IEnumerable<IFaceEnrollmentExtractor> extractors, TimeProvider timeProvider, IPublicIdGenerator publicIds,
    ILogger<BiometricEnrollmentService> logger)
{
    public const string ManagePermission = "biometrics.manage";
    private const int ExpiryBatch = 100;

    public async Task<IReadOnlyList<BiometricEnrollmentSummary>> ListAsync(Guid actorId, string? userPublicId,
        CancellationToken cancellationToken)
    {
        var target = await ResolveTargetAsync(actorId, userPublicId, cancellationToken);
        await ExpireDueAsync(cancellationToken);
        var rows = await database.BiometricEnrollments.AsNoTracking()
            .Where(item => item.UserId == target.Id)
            .OrderByDescending(item => item.CreatedAtUtc).ThenBy(item => item.Id)
            .Take(20).ToArrayAsync(cancellationToken);
        return rows.Select(item => Map(item, target.PublicId!)).ToArray();
    }

    public async Task<BiometricEnrollmentSummary> EnrollAsync(Guid actorId, string? userPublicId, FaceCropV1 crop,
        string? consentVersion, CancellationToken cancellationToken)
    {
        if (!crop.IsValid) throw new ArgumentException("Recorte facial inválido.");
        if (!string.Equals(consentVersion, BiometricPolicy.ConsentVersion, StringComparison.Ordinal))
            throw new BiometricConsentRequiredException();
        var extractor = extractors.FirstOrDefault()
            ?? throw new BiometricEnrollmentUnavailableException("face_extractor_not_configured");
        if (!protector.IsConfigured)
            throw new BiometricEnrollmentUnavailableException("biometric_protection_not_configured");
        var target = await ResolveTargetAsync(actorId, userPublicId, cancellationToken);
        if (!target.IsActive) throw new ArgumentException("Conta de destino inativa.");

        FaceEmbeddingV1 embedding;
        try
        {
            embedding = await extractor.ExtractAsync(crop, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FaceExtractionException exception)
        {
            if (exception.Code == "face_extractor_not_configured")
                throw new BiometricEnrollmentUnavailableException("face_extractor_not_configured");
            throw new BiometricEnrollmentRejectedException(exception.Code);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Falha inesperada na extração facial; tipo={ExceptionType}", exception.GetType().Name);
            throw new BiometricEnrollmentUnavailableException("face_extraction_failed");
        }
        if (!embedding.IsValid) throw new ArgumentException("Embedding facial inválido.");

        var protectedData = protector.Protect(Serialize(embedding.Values));
        var now = timeProvider.GetUtcNow();
        try
        {
            var created = await unitOfWork.ExecuteAtomicallyAsync(async token =>
            {
                var current = await database.BiometricEnrollments
                    .Where(item => item.UserId == target.Id && item.Status == BiometricEnrollment.ActiveStatus)
                    .ToArrayAsync(token);
                foreach (var previous in current)
                {
                    previous.Status = BiometricEnrollment.RevokedStatus;
                    previous.RevokedAtUtc = now;
                    previous.RevokedReason = "replaced";
                    ClearPayload(previous);
                    audit.Append(actorId, target.Id, "biometric.enrollment_revoked", previous.PublicId, now);
                }
                var enrollment = new BiometricEnrollment
                {
                    Id = Guid.NewGuid(),
                    PublicId = publicIds.Generate(PublicIdPrefixes.BiometricEnrollmentPrefix),
                    UserId = target.Id,
                    Status = BiometricEnrollment.ActiveStatus,
                    EnrolledAtUtc = now,
                    ExpiresAtUtc = now.Add(BiometricPolicy.Retention),
                    EmbeddingCiphertext = protectedData.Ciphertext,
                    EmbeddingNonce = protectedData.Nonce,
                    EmbeddingTag = protectedData.Tag,
                    KeyVersion = protectedData.KeyVersion,
                    Dimensions = embedding.Dimensions,
                    ModelId = embedding.ModelId,
                    ModelVersion = embedding.ModelVersion,
                    SchemaVersion = embedding.SchemaVersion,
                    AdapterId = extractor.AdapterId,
                    ConsentVersion = BiometricPolicy.ConsentVersion,
                    ConsentAcceptedAtUtc = now,
                    CreatedByUserId = actorId,
                    CreatedAtUtc = now
                };
                database.BiometricEnrollments.Add(enrollment);
                audit.Append(actorId, target.Id, "biometric.enrollment_created", enrollment.PublicId, now);
                return enrollment;
            }, cancellationToken);
            return Map(created, target.PublicId!);
        }
        catch (DbUpdateException)
        {
            throw new BiometricEnrollmentConflictException("biometric_enrollment_conflict");
        }
    }

    public async Task RevokeAsync(Guid actorId, string enrollmentPublicId, CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(enrollmentPublicId, PublicIdPrefixes.BiometricEnrollmentPrefix))
            throw new KeyNotFoundException();
        var isOwner = await database.BiometricEnrollments.AsNoTracking()
            .AnyAsync(item => item.PublicId == enrollmentPublicId && item.UserId == actorId, cancellationToken);
        if (!isOwner && !await CanManageAsync(actorId, cancellationToken))
            throw new UnauthorizedAccessException();
        await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var enrollment = await database.BiometricEnrollments
                .SingleOrDefaultAsync(item => item.PublicId == enrollmentPublicId, token)
                ?? throw new KeyNotFoundException();
            if (enrollment.Status != BiometricEnrollment.ActiveStatus) return true;
            var now = timeProvider.GetUtcNow();
            enrollment.Status = BiometricEnrollment.RevokedStatus;
            enrollment.RevokedAtUtc = now;
            enrollment.RevokedReason = isOwner ? "owner_request" : "administrator_request";
            ClearPayload(enrollment);
            audit.Append(actorId, enrollment.UserId, "biometric.enrollment_revoked", enrollment.PublicId, now);
            return true;
        }, cancellationToken);
    }

    /// <summary>Erases expired embeddings. Durable because the absolute expiry is persisted with the row.</summary>
    public async Task<int> ExpireDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var due = await database.BiometricEnrollments
            .Where(item => item.Status == BiometricEnrollment.ActiveStatus && item.ExpiresAtUtc <= now)
            .OrderBy(item => item.ExpiresAtUtc).Take(ExpiryBatch).ToArrayAsync(cancellationToken);
        if (due.Length == 0) return 0;
        return await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            foreach (var enrollment in due)
            {
                enrollment.Status = BiometricEnrollment.ExpiredStatus;
                enrollment.RevokedAtUtc = now;
                enrollment.RevokedReason = "expired";
                ClearPayload(enrollment);
                audit.Append(null, enrollment.UserId, "biometric.enrollment_expired", enrollment.PublicId, now);
            }
            await Task.CompletedTask;
            return due.Length;
        }, cancellationToken);
    }

    private async Task<ApplicationUser> ResolveTargetAsync(Guid actorId, string? userPublicId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userPublicId))
            return await database.Users.SingleAsync(item => item.Id == actorId, cancellationToken);
        if (!PublicIdFormat.IsValid(userPublicId, PublicIdPrefixes.UserPrefix)) throw new KeyNotFoundException();
        var target = await database.Users.SingleOrDefaultAsync(item => item.PublicId == userPublicId, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (target.Id != actorId && !await CanManageAsync(actorId, cancellationToken))
            throw new UnauthorizedAccessException();
        return target;
    }

    private Task<bool> CanManageAsync(Guid actorId, CancellationToken cancellationToken) =>
        profiles.IsAllowedAnyCameraAsync(actorId, ManagePermission, cancellationToken);

    private static byte[] Serialize(IReadOnlyList<float> values)
    {
        var bytes = new byte[values.Count * sizeof(float)];
        for (var index = 0; index < values.Count; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * sizeof(float)), values[index]);
        return bytes;
    }

    private static void ClearPayload(BiometricEnrollment enrollment)
    {
        enrollment.EmbeddingCiphertext = [];
        enrollment.EmbeddingNonce = [];
        enrollment.EmbeddingTag = [];
    }

    private static BiometricEnrollmentSummary Map(BiometricEnrollment item, string userPublicId) =>
        new(item.PublicId!, userPublicId, item.Status, item.EnrolledAtUtc, item.ExpiresAtUtc, item.RevokedAtUtc,
            item.RevokedReason ?? string.Empty, item.ModelId, item.ModelVersion, item.Dimensions, item.KeyVersion,
            item.ConsentVersion, item.ConsentAcceptedAtUtc);
}
