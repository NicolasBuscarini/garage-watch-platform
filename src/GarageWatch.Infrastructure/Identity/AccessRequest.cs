namespace GarageWatch.Infrastructure.Identity;

public sealed class AccessRequest : IHasPublicId
{
    public Guid Id { get; set; }
    public string? PublicId { get; set; }
    public Guid UserId { get; set; }
    public Guid IdempotencyKey { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public ApplicationUser? User { get; set; }
}

public static class AccessRequestPurposes
{
    public const string LiveMonitoring = "live_monitoring";
    public const string CameraAccess = "camera_access";

    public static bool IsValid(string value) => value is LiveMonitoring or CameraAccess;
}
