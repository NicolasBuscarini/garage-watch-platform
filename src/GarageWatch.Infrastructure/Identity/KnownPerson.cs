namespace GarageWatch.Infrastructure.Identity;

/// <summary>Minimal consent-backed roster record; biometric samples and embeddings are deliberately absent.</summary>
public sealed class KnownPerson : IHasPublicId
{
    public Guid Id { get; set; }
    public string? PublicId { get; set; }
    public string? DisplayName { get; set; }
    public string? Purpose { get; set; }
    public DateTimeOffset? AuthorizationRecordedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? DeletedAtUtc { get; set; }
    public long Version { get; set; }
}
