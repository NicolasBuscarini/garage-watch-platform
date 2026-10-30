namespace GarageWatch.Infrastructure.Identity;

public sealed class CameraContext
{
    public string ContextId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public ICollection<CameraCatalogEntry> Cameras { get; set; } = new List<CameraCatalogEntry>();
}
