namespace GarageWatch.Infrastructure.Identity;

/// <summary>
/// One encrypted face embedding owned by an account. Only ciphertext, nonce, tag and key version are
/// persisted; plaintext embeddings and uploaded images are never stored.
/// </summary>
public sealed class BiometricEnrollment : IHasPublicId
{
    public const string ActiveStatus = "active";
    public const string RevokedStatus = "revoked";
    public const string ExpiredStatus = "expired";

    public Guid Id { get; set; }
    public string? PublicId { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser? User { get; set; }
    public string Status { get; set; } = ActiveStatus;
    public DateTimeOffset EnrolledAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public string? RevokedReason { get; set; }
    public byte[] EmbeddingCiphertext { get; set; } = [];
    public byte[] EmbeddingNonce { get; set; } = [];
    public byte[] EmbeddingTag { get; set; } = [];
    public string KeyVersion { get; set; } = string.Empty;
    public int Dimensions { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public string AdapterId { get; set; } = string.Empty;
    public string ConsentVersion { get; set; } = string.Empty;
    public DateTimeOffset ConsentAcceptedAtUtc { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
