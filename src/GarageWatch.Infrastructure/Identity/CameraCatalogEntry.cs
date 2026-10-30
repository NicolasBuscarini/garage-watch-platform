namespace GarageWatch.Infrastructure.Identity;

public sealed class CameraCatalogEntry
{
    public string CameraId { get; set; } = string.Empty;
    public string ContextId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public CameraContext Context { get; set; } = null!;
}
