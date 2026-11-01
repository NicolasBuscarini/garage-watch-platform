namespace GarageWatch.Infrastructure.Identity;

public sealed class ClipRetentionPolicy
{
    public string CameraId { get; set; } = string.Empty;
    public int RetentionDays { get; set; } = 15;
    public long? MaximumStorageBytes { get; set; }
    public long Version { get; set; }
    public string UpdatedByUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ClipPreservation
{
    public string ClipId { get; set; } = string.Empty;
    public string PublicId { get; set; } = string.Empty;
    public string CameraId { get; set; } = string.Empty;
    public bool IsPreserved { get; set; }
    public long Version { get; set; }
    public string UpdatedByUserId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
