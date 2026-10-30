namespace GarageWatch.Application.Authorization;

public static class PermissionCatalog
{
    public const string Version = "1.0";

    private static readonly IReadOnlyDictionary<string, PermissionDefinition> Entries =
        new Dictionary<string, PermissionDefinition>(StringComparer.Ordinal)
        {
            ["camera.list"] = new("camera.list", "List authorized cameras", true),
            ["live.video.view"] = new("live.video.view", "View live video", true),
            // Keep non-delegable until the server can ensure video-only bytes for other profiles.
            ["live.audio.listen"] = new("live.audio.listen", "Listen to live audio", false),
            ["events.read"] = new("events.read", "Read events", true),
            ["clips.read"] = new("clips.read", "Read clips", true),
            ["clips.export"] = new("clips.export", "Export clips", true),
            ["admin.users.manage"] = new("admin.users.manage", "Manage accounts and access", false),
            ["admin.permissions.manage"] = new("admin.permissions.manage", "Manage permission profiles", false),
            ["admin.configuration.manage"] = new("admin.configuration.manage", "Manage privileged configuration", false),
            ["admin.system.health.read"] = new("admin.system.health.read", "Read detailed system health", false),
            ["admin.models.promote"] = new("admin.models.promote", "Promote evaluated models", false)
        };

    private static readonly IReadOnlyCollection<PermissionDefinition> PublicEntries =
        Array.AsReadOnly(Entries.Values.ToArray());

    public static IReadOnlyCollection<PermissionDefinition> All => PublicEntries;

    public static bool TryGet(string key, out PermissionDefinition definition) => Entries.TryGetValue(key, out definition!);
}

public sealed record PermissionDefinition(string Key, string Description, bool IsDelegable);

public sealed record PermissionScope(string ContextId, string CameraId);

public sealed record PermissionGrant(string PermissionKey, IReadOnlySet<PermissionScope> Scopes);

public sealed record AuthorizationSubject(bool IsActive, IReadOnlySet<string> Roles,
    IReadOnlyCollection<PermissionGrant> Grants);

public sealed record PermissionRequest(string PermissionKey, string? ContextId = null, string? CameraId = null);

public interface IPermissionEvaluator
{
    bool IsAllowed(AuthorizationSubject subject, PermissionRequest request);
}

public sealed class PermissionEvaluator : IPermissionEvaluator
{
    public bool IsAllowed(AuthorizationSubject subject, PermissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(request);

        if (!subject.IsActive || !PermissionCatalog.TryGet(request.PermissionKey, out var definition))
            return false;

        if (subject.Roles.Contains("Administrator", StringComparer.Ordinal))
            return true;

        if (!definition.IsDelegable || string.IsNullOrWhiteSpace(request.ContextId) ||
            string.IsNullOrWhiteSpace(request.CameraId))
            return false;

        return subject.Grants.Any(grant =>
            StringComparer.Ordinal.Equals(grant.PermissionKey, definition.Key) &&
            grant.Scopes.Contains(new PermissionScope(request.ContextId, request.CameraId)));
    }
}
