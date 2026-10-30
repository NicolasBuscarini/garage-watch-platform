namespace GarageWatch.Infrastructure.Identity;

public sealed class CameraAccessGrant
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string ContextId { get; set; } = string.Empty;
    public string CameraId { get; set; } = string.Empty;
    public Guid GrantedByUserId { get; set; }
    public DateTimeOffset GrantedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public ApplicationUser? User { get; set; }
}
