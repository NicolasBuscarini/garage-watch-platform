using GarageWatch.Application.Identity;
using GarageWatch.Infrastructure.Identity.Repositories;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed record FaceExtractorCandidateInput(string? ModelId, string? ModelVersion, string? ArtifactSha256,
    string? SourceUri, string? CodeLicense, string? WeightsLicense, string? AdapterId,
    string? AdapterContractVersion);

public sealed record FaceExtractorCandidateSummary(string PublicId, string ModelId, string ModelVersion,
    string ArtifactSha256, string SourceUri, string CodeLicense, string WeightsLicense, string AdapterId,
    string AdapterContractVersion, DateTimeOffset CreatedAtUtc);

public sealed record FaceExtractorRegistrySnapshot(long Version, FaceExtractorCandidateSummary? SelectedCandidate,
    IReadOnlyList<FaceExtractorCandidateSummary> Candidates, bool RuntimeEnabled, bool SampleCollectionEnabled);

public sealed record FaceExtractorSelectionResult(long Version, FaceExtractorCandidateSummary SelectedCandidate,
    bool RuntimeEnabled, bool SampleCollectionEnabled);

public sealed class FaceExtractorConflictException(string code) : Exception(code);

public sealed class FaceExtractorRegistryService(IamDbContext database, IIamUnitOfWork unitOfWork,
    IIamAuditEventRepository auditEvents, TimeProvider timeProvider)
{
    public const string RegistryKey = "face-extractor";
    public const string AdapterContract = "face-embedding.v1";

    public async Task<FaceExtractorRegistrySnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var state = await database.FaceExtractorSelections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Key == RegistryKey, cancellationToken);
        var candidates = await database.FaceExtractorCandidates.AsNoTracking()
            .OrderByDescending(item => item.CreatedAtUtc).ThenBy(item => item.Id)
            .Take(100).ToArrayAsync(cancellationToken);
        var selected = state?.CandidateId is { } id
            ? candidates.FirstOrDefault(item => item.Id == id) ?? await database.FaceExtractorCandidates.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            : null;
        return new(state?.Version ?? 0, selected is null ? null : Map(selected), candidates.Select(Map).ToArray(),
            RuntimeEnabled: false, SampleCollectionEnabled: false);
    }

    public async Task<FaceExtractorCandidateSummary> CreateCandidateAsync(Guid actorId,
        FaceExtractorCandidateInput input, CancellationToken cancellationToken)
    {
        var normalized = ValidateAndNormalize(input);
        return await unitOfWork.ExecuteAtomicallyAsync<FaceExtractorCandidateSummary>(async token =>
        {
            var duplicate = await database.FaceExtractorCandidates.AnyAsync(item =>
                item.ModelId == normalized.ModelId && item.ModelVersion == normalized.ModelVersion &&
                item.ArtifactSha256 == normalized.ArtifactSha256, token);
            if (duplicate) throw new FaceExtractorConflictException("face_extractor_candidate_exists");
            var candidate = new FaceExtractorCandidate
            {
                Id = Guid.NewGuid(),
                ModelId = normalized.ModelId,
                ModelVersion = normalized.ModelVersion,
                ArtifactSha256 = normalized.ArtifactSha256,
                SourceUri = normalized.SourceUri,
                CodeLicense = normalized.CodeLicense,
                WeightsLicense = normalized.WeightsLicense,
                AdapterId = normalized.AdapterId,
                AdapterContractVersion = normalized.AdapterContractVersion,
                CreatedAtUtc = timeProvider.GetUtcNow(),
                CreatedByUserId = actorId
            };
            database.FaceExtractorCandidates.Add(candidate);
            await database.SaveChangesAsync(token);
            auditEvents.Append(actorId, null, "face_extractor.candidate_created", candidate.PublicId,
                candidate.CreatedAtUtc);
            return Map(candidate);
        }, cancellationToken);
    }

    public async Task<FaceExtractorSelectionResult> SelectForEvaluationAsync(Guid actorId, string publicId,
        long expectedVersion, CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(publicId, PublicIdPrefixes.FaceExtractorPrefix) || expectedVersion < 0)
            throw new ArgumentException("Seleção de extrator inválida.");
        return await unitOfWork.ExecuteAtomicallyAsync<FaceExtractorSelectionResult>(async token =>
        {
            var candidate = await database.FaceExtractorCandidates.SingleOrDefaultAsync(
                item => item.PublicId == publicId, token)
                ?? throw new KeyNotFoundException("Candidato de extrator não encontrado.");
            var state = await database.FaceExtractorSelections.SingleOrDefaultAsync(
                item => item.Key == RegistryKey, token);
            var currentVersion = state?.Version ?? 0;
            if (currentVersion != expectedVersion)
                throw new FaceExtractorConflictException("face_extractor_selection_version_conflict");
            if (state?.CandidateId != candidate.Id)
            {
                var now = timeProvider.GetUtcNow();
                if (state is null)
                {
                    state = new FaceExtractorSelection { Key = RegistryKey, Version = 1 };
                    database.FaceExtractorSelections.Add(state);
                }
                else
                {
                    state.Version++;
                }
                state.CandidateId = candidate.Id;
                state.UpdatedAtUtc = now;
                state.UpdatedByUserId = actorId;
                auditEvents.Append(actorId, null, "face_extractor.selected_for_evaluation", candidate.PublicId, now);
            }
            await database.SaveChangesAsync(token);
            return new(state!.Version, Map(candidate), RuntimeEnabled: false, SampleCollectionEnabled: false);
        }, cancellationToken);
    }

    private static NormalizedCandidate ValidateAndNormalize(FaceExtractorCandidateInput input)
    {
        var modelId = Normalize(input.ModelId, 128);
        var modelVersion = Normalize(input.ModelVersion, 64);
        var hash = Normalize(input.ArtifactSha256, 64).ToLowerInvariant();
        var source = Normalize(input.SourceUri, 512);
        var codeLicense = Normalize(input.CodeLicense, 128);
        var weightsLicense = Normalize(input.WeightsLicense, 128);
        var adapterId = Normalize(input.AdapterId, 64);
        var adapterContract = Normalize(input.AdapterContractVersion, 64);
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || adapterContract != AdapterContract ||
            !Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Metadados do candidato de extrator inválidos.");
        return new(modelId, modelVersion, hash, uri.AbsoluteUri, codeLicense, weightsLicense, adapterId,
            adapterContract);
    }

    private static string Normalize(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximumLength ||
            normalized.Any(char.IsControl))
            throw new ArgumentException("Metadados do candidato de extrator inválidos.");
        return normalized;
    }

    private static FaceExtractorCandidateSummary Map(FaceExtractorCandidate item) =>
        new(item.PublicId!, item.ModelId, item.ModelVersion, item.ArtifactSha256, item.SourceUri, item.CodeLicense,
            item.WeightsLicense, item.AdapterId, item.AdapterContractVersion, item.CreatedAtUtc);

    private sealed record NormalizedCandidate(string ModelId, string ModelVersion, string ArtifactSha256,
        string SourceUri, string CodeLicense, string WeightsLicense, string AdapterId,
        string AdapterContractVersion);
}
