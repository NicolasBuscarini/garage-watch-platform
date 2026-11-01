namespace GarageWatch.Api.Contracts;

public sealed record CreateKnownPersonRequest(string? DisplayName, string? Purpose, bool AuthorizationConfirmed);
public sealed record KnownPersonSummaryResponse(string PublicId, string DisplayName, string Purpose,
    DateTimeOffset AuthorizationRecordedAtUtc, DateTimeOffset CreatedAtUtc, long Version);
public sealed record KnownPersonPageResponse(IReadOnlyList<KnownPersonSummaryResponse> Items, int Page, int PageSize, int Total);
public sealed record CreateFaceExtractorCandidateRequest(string? ModelId, string? ModelVersion, string? ArtifactSha256,
    string? SourceUri, string? CodeLicense, string? WeightsLicense, string? AdapterId,
    string? AdapterContractVersion);
public sealed record SelectFaceExtractorRequest(string? CandidatePublicId, long ExpectedVersion);
