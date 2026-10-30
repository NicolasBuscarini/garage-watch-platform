namespace GarageWatch.Infrastructure.Identity;

public sealed class AccessProfile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public ICollection<AccessProfileVersion> Versions { get; set; } = new List<AccessProfileVersion>();
}

public sealed class AccessProfileVersion
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public long Version { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid CreatedByUserId { get; set; }
    public AccessProfile? Profile { get; set; }
    public ICollection<AccessProfilePermission> Permissions { get; set; } = new List<AccessProfilePermission>();
}

public sealed class AccessProfilePermission
{
    public Guid AccessProfileVersionId { get; set; }
    public string PermissionKey { get; set; } = string.Empty;
    public AccessProfileVersion? Version { get; set; }
}

public sealed class UserAccessProfileAssignment
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid AccessProfileVersionId { get; set; }
    public Guid AssignedByUserId { get; set; }
    public DateTimeOffset AssignedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public ApplicationUser? User { get; set; }
    public AccessProfileVersion? ProfileVersion { get; set; }
    public ICollection<UserAccessProfileScope> Scopes { get; set; } = new List<UserAccessProfileScope>();
}

public sealed class UserAccessProfileScope
{
    public Guid AssignmentId { get; set; }
    public string ContextId { get; set; } = string.Empty;
    public string CameraId { get; set; } = string.Empty;
    public UserAccessProfileAssignment? Assignment { get; set; }
}

public sealed class IamMigrationState
{
    public string Key { get; set; } = string.Empty;
    public DateTimeOffset CompletedAtUtc { get; set; }
}
