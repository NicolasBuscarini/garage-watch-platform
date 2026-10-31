namespace GarageWatch.Infrastructure.Identity;

public sealed class CameraCatalogEntry : IHasPublicId
{
    public string CameraId { get; set; } = string.Empty;
    public string? PublicId { get; set; }
    public string ContextId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool ClipTriggerEnabled { get; set; }
    public long ClipTriggerVersion { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public CameraContext Context { get; set; } = null!;
}
