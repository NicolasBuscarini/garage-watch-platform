namespace GarageWatch.Infrastructure.Identity;

public sealed class IamAuditEvent
{
    public Guid Id { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? SubjectUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public ApplicationUser? Actor { get; set; }
    public ApplicationUser? Subject { get; set; }
}
