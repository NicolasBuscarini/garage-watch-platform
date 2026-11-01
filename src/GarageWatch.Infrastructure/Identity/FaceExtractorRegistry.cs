namespace GarageWatch.Infrastructure.Identity;

/// <summary>Model metadata only; selecting a profile never enables inference or biometric collection.</summary>
public sealed class FaceExtractorCandidate : IHasPublicId
{
    public Guid Id { get; set; }
    public string? PublicId { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string ArtifactSha256 { get; set; } = string.Empty;
    public string SourceUri { get; set; } = string.Empty;
    public string CodeLicense { get; set; } = string.Empty;
    public string WeightsLicense { get; set; } = string.Empty;
    public string AdapterId { get; set; } = string.Empty;
    public string AdapterContractVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid CreatedByUserId { get; set; }
}

/// <summary>Versioned operator choice for evaluation; it is deliberately not an active runtime setting.</summary>
public sealed class FaceExtractorSelection
{
    public string Key { get; set; } = "face-extractor";
    public Guid? CandidateId { get; set; }
    public long Version { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public Guid? UpdatedByUserId { get; set; }
}
