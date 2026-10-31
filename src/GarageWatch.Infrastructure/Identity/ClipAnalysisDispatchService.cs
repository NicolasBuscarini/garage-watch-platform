using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Application.Authorization;
using GarageWatch.Application.Identity;
using GarageWatch.Infrastructure.Capture;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed record ClipAnalysisPolicyResult(string CameraId, string ContextId, string Policy, long Version,
    DateTimeOffset UpdatedAtUtc);
public sealed record ClipAnalysisPolicyView(string CameraId, string ContextId, string Policy, long Version,
    DateTimeOffset? UpdatedAtUtc, bool CanRequest, bool CanManage);
public sealed record ClipAnalysisJobResult(string PublicId, string ClipPublicId, string AnalysisVersion, string State,
    bool Existing);
public sealed class ClipAnalysisConflictException(string code) : Exception(code);
public sealed class ClipAnalysisDeniedException(string code) : Exception(code);
public sealed class ClipAnalysisCapacityException : Exception;

public sealed class ClipAnalysisDispatchService(IamDbContext database, IIamUnitOfWork unitOfWork,
    IIamAuditEventRepository audit, AccessProfileService profiles, CaptureOptions capture,
    ClipAnalysisDispatchOptions options, TimeProvider timeProvider, IPublicIdGenerator publicIds)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ClipAnalysisPolicyView> GetPolicyAsync(Guid actorId, string cameraId,
        CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(cameraId, PublicIdPrefixes.CameraPrefix)) throw new KeyNotFoundException();
        var scope = await FindEnabledCameraByPublicIdAsync(cameraId, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (!await profiles.IsAllowedAsync(actorId,
                new PermissionRequest("clips.read", scope.ContextId, scope.CameraId), cancellationToken))
            throw new KeyNotFoundException();
        var policy = await database.ClipAnalysisDispatchPolicies.AsNoTracking().SingleOrDefaultAsync(
            item => item.CameraId == scope.CameraId, cancellationToken);
        var canRequest = await profiles.IsAllowedAsync(actorId,
            new PermissionRequest("clips.analysis.request", scope.ContextId, scope.CameraId), cancellationToken);
        var canManage = await profiles.IsAllowedAsync(actorId,
            new PermissionRequest("clips.analysis.policy.manage", scope.ContextId, scope.CameraId), cancellationToken);
        return new(scope.CameraPublicId, scope.ContextPublicId, policy?.Policy ?? ClipAnalysisPolicies.ManualOnly,
            policy?.Version ?? 0, policy?.UpdatedAtUtc, canRequest, canManage);
    }

    public async Task<ClipAnalysisPolicyResult> UpdatePolicyAsync(Guid actorId, string cameraId, long expectedVersion,
        string policy, CancellationToken cancellationToken)
    {
        if (expectedVersion < 0 || !ClipAnalysisPolicies.IsValid(policy))
            throw new ArgumentException("Política ou versão inválida.");

        if (!PublicIdFormat.IsValid(cameraId, PublicIdPrefixes.CameraPrefix)) throw new KeyNotFoundException();
        var scope = await FindEnabledCameraByPublicIdAsync(cameraId, cancellationToken) ?? throw new KeyNotFoundException();
        if (!await profiles.IsAllowedAsync(actorId, new PermissionRequest("clips.analysis.policy.manage", scope.ContextId,
                scope.CameraId), cancellationToken)) throw new UnauthorizedAccessException();

        return await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var current = await database.ClipAnalysisDispatchPolicies.SingleOrDefaultAsync(item =>
                item.CameraId == scope.CameraId, token);
            var currentVersion = current?.Version ?? 0;
            if (currentVersion != expectedVersion) throw new ClipAnalysisConflictException("policy_version_conflict");
            var now = timeProvider.GetUtcNow();
            if (current is null)
            {
                current = new ClipAnalysisDispatchPolicy
                {
                    CameraId = scope.CameraId,
                    ContextId = scope.ContextId,
                    Policy = policy,
                    Version = 1,
                    UpdatedByUserId = actorId.ToString("D"),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                database.ClipAnalysisDispatchPolicies.Add(current);
            }
            else
            {
                current.Policy = policy;
                current.Version = checked(current.Version + 1);
                current.UpdatedByUserId = actorId.ToString("D");
                current.UpdatedAtUtc = now;
            }
            audit.Append(actorId, actorId, "clip.analysis.policy.updated", cameraId, now);
            return new ClipAnalysisPolicyResult(scope.CameraPublicId, scope.ContextPublicId, current.Policy, current.Version,
                current.UpdatedAtUtc);
        }, cancellationToken);
    }

    public async Task<ClipAnalysisJobResult> RequestAsync(Guid actorId, string clipId, Guid idempotencyKey,
        string? requestedAnalysisVersion, CancellationToken cancellationToken)
    {
        if (idempotencyKey == Guid.Empty || !PublicIdFormat.IsValid(clipId, PublicIdPrefixes.ClipPrefix) ||
            (requestedAnalysisVersion is not null && !ClipAnalysisDispatchOptions.IsSafeVersion(requestedAnalysisVersion)))
            throw new ArgumentException("Pedido de análise inválido.");
        var clip = await ReadReadyClipByPublicIdAsync(clipId, cancellationToken) ?? throw new KeyNotFoundException();
        var scope = await FindEnabledCameraAsync(clip.CameraId, cancellationToken) ?? throw new KeyNotFoundException();
        if (!await profiles.IsAllowedAsync(actorId, new PermissionRequest("clips.analysis.request", scope.ContextId,
                scope.CameraId), cancellationToken))
        {
            await unitOfWork.ExecuteAtomicallyAsync(token =>
            {
                Audit(actorId, "clip.analysis.request.denied", clip.ClipId);
                return Task.CompletedTask;
            }, cancellationToken);
            throw new UnauthorizedAccessException();
        }
        try
        {
            return await EnqueueAsync(clip, scope, idempotencyKey, requestedAnalysisVersion ?? options.DefaultAnalysisVersion,
                "manual", actorId, false, cancellationToken);
        }
        catch (ClipAnalysisDeniedException)
        {
            await unitOfWork.ExecuteAtomicallyAsync(token =>
            {
                Audit(actorId, "clip.analysis.request.denied", clip.ClipId);
                return Task.CompletedTask;
            }, cancellationToken);
            throw;
        }
    }

    public async Task AutoRequestAsync(ClipManifestV1 clip, CancellationToken cancellationToken)
    {
        if (clip.State != "ready" || !await IsMediaIntactAsync(clip, cancellationToken)) return;
        var scope = await FindEnabledCameraAsync(clip.CameraId, cancellationToken);
        if (scope is null) return;
        var policy = await database.ClipAnalysisDispatchPolicies.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CameraId == scope.CameraId, cancellationToken);
        if (policy is null || policy.Policy == ClipAnalysisPolicies.ManualOnly || policy.Policy == ClipAnalysisPolicies.Never)
            return;
        if (policy.Policy == ClipAnalysisPolicies.LargeMotion && !HasVersionedLargeMotionEvidence(clip))
            return;
        try
        {
            await EnqueueAsync(clip, scope, DeterministicKey(clip.ClipId, options.DefaultAnalysisVersion),
                options.DefaultAnalysisVersion, "automatic", null, true, cancellationToken);
        }
        catch (ClipAnalysisCapacityException)
        {
            // Analysis capacity is intentionally isolated from capture and clip assembly.
        }
        catch (ClipAnalysisDeniedException)
        {
            // A concurrent policy change to never must not affect the capture or clip lifecycle.
        }
    }

    public Task<ClipAnalysisJobResult?> ClaimNextAsync(CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var now = timeProvider.GetUtcNow();
            await ReconcileExpiredLeasesAsync(now, token);
            var job = database.ClipAnalysisJobs.Local.FirstOrDefault(item => item.State == "analysis_pending") ??
                await database.ClipAnalysisJobs.OrderBy(item => item.CreatedAtUtc)
                    .FirstOrDefaultAsync(item => item.State == "analysis_pending", token);
            if (job is null) return null;
            job.State = "processing";
            job.LeaseId = Guid.NewGuid();
            job.LeaseExpiresAtUtc = now.AddSeconds(options.LeaseSeconds);
            job.AttemptCount = checked(job.AttemptCount + 1);
            job.UpdatedAtUtc = now;
            var clip = await ReadReadyClipByInternalIdAsync(job.ClipId, token);
            return clip is null ? null : Map(job, false, clip.PublicId!);
        }, cancellationToken);

    private async Task<ClipAnalysisJobResult> EnqueueAsync(ClipManifestV1 clip, CameraScope scope, Guid key,
        string analysisVersion, string requestKind, Guid? actorId, bool automatic, CancellationToken cancellationToken) =>
        await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            await ReconcileExpiredLeasesAsync(timeProvider.GetUtcNow(), token);
            var policy = await database.ClipAnalysisDispatchPolicies.SingleOrDefaultAsync(item =>
                item.CameraId == scope.CameraId, token);
            var effectivePolicy = policy?.Policy ?? ClipAnalysisPolicies.ManualOnly;
            if (effectivePolicy == ClipAnalysisPolicies.Never)
            {
                throw new ClipAnalysisDeniedException("analysis_not_allowed");
            }
            if (automatic && effectivePolicy == ClipAnalysisPolicies.ManualOnly) return null!;
            if (automatic && effectivePolicy == ClipAnalysisPolicies.LargeMotion && !HasVersionedLargeMotionEvidence(clip))
                return null!;

            var byKey = await database.ClipAnalysisJobs.SingleOrDefaultAsync(job =>
                job.ClipId == clip.ClipId && job.IdempotencyKey == key, token);
            if (byKey is not null)
            {
                if (!string.Equals(byKey.AnalysisVersion, analysisVersion, StringComparison.Ordinal))
                    throw new ClipAnalysisConflictException("idempotency_conflict");
                Audit(actorId, "clip.analysis.request.idempotent", clip.ClipId);
                return Map(byKey, true, clip.PublicId!);
            }
            var existing = await database.ClipAnalysisJobs.SingleOrDefaultAsync(job =>
                job.ClipId == clip.ClipId && job.AnalysisVersion == analysisVersion, token);
            if (existing is not null)
            {
                Audit(actorId, "clip.analysis.request.deduplicated", clip.ClipId);
                return Map(existing, true, clip.PublicId!);
            }
            var queued = await database.ClipAnalysisJobs.CountAsync(job => job.State == "analysis_pending" ||
                job.State == "processing", token);
            if (queued >= options.QueueCapacity)
            {
                Audit(actorId, "clip.analysis.request.capacity", clip.ClipId);
                throw new ClipAnalysisCapacityException();
            }
            var now = timeProvider.GetUtcNow();
            var job = new ClipAnalysisJob
            {
                Id = Guid.NewGuid(),
                PublicId = publicIds.Generate(PublicIdPrefixes.ClipAnalysisJobPrefix),
                ClipId = clip.ClipId,
                ContextId = scope.ContextId,
                CameraId = scope.CameraId,
                IdempotencyKey = key,
                AnalysisVersion = analysisVersion,
                PolicyVersion = policy?.Version ?? 0,
                RequestKind = requestKind,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            database.ClipAnalysisJobs.Add(job);
            Audit(actorId, automatic ? "clip.analysis.auto.requested" : "clip.analysis.requested", clip.ClipId);
            return Map(job, false, clip.PublicId!);
        }, cancellationToken);

    private async Task<CameraScope?> FindEnabledCameraAsync(string cameraId, CancellationToken cancellationToken) =>
        await database.CameraCatalog.AsNoTracking().Where(camera => camera.CameraId == cameraId && camera.IsEnabled &&
            camera.Context.IsEnabled).Select(camera => new CameraScope(camera.ContextId, camera.CameraId,
                camera.PublicId!, camera.Context.PublicId!))
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<CameraScope?> FindEnabledCameraByPublicIdAsync(string cameraPublicId,
        CancellationToken cancellationToken) =>
        await database.CameraCatalog.AsNoTracking().Where(camera => camera.PublicId == cameraPublicId &&
            camera.IsEnabled && camera.Context.IsEnabled).Select(camera => new CameraScope(camera.ContextId,
            camera.CameraId, camera.PublicId!, camera.Context.PublicId!)).SingleOrDefaultAsync(cancellationToken);

    private async Task<ClipManifestV1?> ReadReadyClipByPublicIdAsync(string publicId, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(capture.SpoolDirectory, "clips", "manifests");
        if (!Directory.Exists(directory)) return null;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Take(10_000))
        {
            var candidate = await ReadReadyClipByPathAsync(path, cancellationToken);
            if (candidate?.PublicId == publicId) return candidate;
        }
        return null;
    }

    private async Task<ClipManifestV1?> ReadReadyClipByInternalIdAsync(string internalId,
        CancellationToken cancellationToken)
    {
        if (!LocalSegmentSpool.IsSafeSegmentId(internalId)) return null;
        return await ReadReadyClipByPathAsync(Path.Combine(capture.SpoolDirectory, "clips", "manifests",
            internalId + ".json"), cancellationToken);
    }

    private async Task<ClipManifestV1?> ReadReadyClipByPathAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var clip = await JsonSerializer.DeserializeAsync<ClipManifestV1>(stream, JsonOptions, cancellationToken);
            return clip is { SchemaVersion: "1.0", State: "ready" } &&
                   PublicIdFormat.IsValid(clip.PublicId, PublicIdPrefixes.ClipPrefix) &&
                   await IsMediaIntactAsync(clip, cancellationToken) ? clip : null;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private async Task<bool> IsMediaIntactAsync(ClipManifestV1 clip, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clip.MediaFileName) || string.IsNullOrWhiteSpace(clip.MediaSha256) ||
            Path.GetFileName(clip.MediaFileName) != clip.MediaFileName) return false;
        var path = Path.Combine(capture.SpoolDirectory, "clips", "media", clip.MediaFileName);
        if (!File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        if (clip.MediaSha256.Length != 64 || !clip.MediaSha256.All(Uri.IsHexDigit)) return false;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(clip.MediaSha256));
    }

    private static bool HasVersionedLargeMotionEvidence(ClipManifestV1 clip) => false;
    private static Guid DeterministicKey(string clipId, string analysisVersion) =>
        new(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{clipId}:{analysisVersion}"))[..16]);
    private void Audit(Guid? actorId, string action, string clipId) => audit.Append(actorId, actorId, action, clipId,
        timeProvider.GetUtcNow());
    private async Task ReconcileExpiredLeasesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expired = await database.ClipAnalysisJobs.Where(job => job.State == "processing" &&
            job.LeaseExpiresAtUtc != null && job.LeaseExpiresAtUtc < now).ToArrayAsync(cancellationToken);
        foreach (var job in expired)
        {
            job.State = "analysis_pending";
            job.LeaseId = null;
            job.LeaseExpiresAtUtc = null;
            job.UpdatedAtUtc = now;
        }
    }
    private static ClipAnalysisJobResult Map(ClipAnalysisJob job, bool existing, string clipPublicId) => new(job.PublicId!, clipPublicId,
        job.AnalysisVersion, job.State, existing);
    private sealed record CameraScope(string ContextId, string CameraId, string CameraPublicId, string ContextPublicId);
}
