using GarageWatch.Application.Authorization;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class PermissionEvaluatorTests
{
    private readonly PermissionEvaluator evaluator = new();

    [Fact]
    public void Catalog_is_finite_versioned_and_separates_delegable_capabilities()
    {
        Assert.Equal("1.1", PermissionCatalog.Version);
        Assert.Equal(PermissionCatalog.All.Count, PermissionCatalog.All.Select(entry => entry.Key)
            .Distinct(StringComparer.Ordinal).Count());
        Assert.True(PermissionCatalog.TryGet("live.video.view", out var video));
        Assert.True(video.IsDelegable);
        Assert.True(PermissionCatalog.TryGet("live.audio.listen", out var audio));
        Assert.False(audio.IsDelegable);
        Assert.True(PermissionCatalog.TryGet("admin.users.manage", out var admin));
        Assert.False(admin.IsDelegable);
        Assert.True(PermissionCatalog.TryGet("biometrics.manage", out var biometrics));
        Assert.True(biometrics.IsDelegable);
    }

    [Fact]
    public void Administrator_has_reserved_authority_but_only_for_registered_permissions_and_active_account()
    {
        var administrator = Subject(true, ["Administrator"]);

        Assert.True(evaluator.IsAllowed(administrator, new("admin.users.manage")));
        Assert.True(evaluator.IsAllowed(administrator, new("live.video.view")));
        Assert.False(evaluator.IsAllowed(administrator, new("admin.unknown")));
        Assert.False(evaluator.IsAllowed(Subject(false, ["Administrator"]), new("live.video.view")));
    }

    [Fact]
    public void Delegated_permission_requires_registered_delegable_key_and_exact_explicit_scope()
    {
        var subject = Subject(true, ["User"],
        [
            new("live.video.view", Set(new PermissionScope("garage", "garage-01"),
                new PermissionScope("other", "garage-02"))),
            new("admin.users.manage", Set(new PermissionScope("garage", "garage-01"))),
            new("biometrics.manage", Set(new PermissionScope("garage", "garage-01"))),
            new("*", Set(new PermissionScope("*", "*")))
        ]);

        Assert.True(evaluator.IsAllowed(subject, new("live.video.view", "garage", "garage-01")));
        Assert.False(evaluator.IsAllowed(subject, new("live.video.view", "garage", "garage-02")));
        Assert.False(evaluator.IsAllowed(subject, new("live.video.view", "other", "garage-01")));
        Assert.False(evaluator.IsAllowed(subject, new("live.video.view", "garage")));
        Assert.False(evaluator.IsAllowed(subject, new("admin.users.manage", "garage", "garage-01")));
        Assert.False(evaluator.IsAllowed(subject, new("future.permission", "garage", "garage-01")));
        Assert.True(evaluator.IsAllowed(subject, new("biometrics.manage", "garage", "garage-01")));
        Assert.False(evaluator.IsAllowed(subject, new("biometrics.manage", "other", "garage-02")));
    }

    [Fact]
    public void Legacy_viewer_role_alone_does_not_grant_resource_permission()
    {
        Assert.False(evaluator.IsAllowed(Subject(true, ["Viewer"]),
            new("live.video.view", "garage", "garage-01")));
    }

    private static AuthorizationSubject Subject(bool active, IEnumerable<string> roles,
        IReadOnlyCollection<PermissionGrant>? grants = null) =>
        new(active, new HashSet<string>(roles, StringComparer.Ordinal), grants ?? []);

    private static IReadOnlySet<PermissionScope> Set(params PermissionScope[] values) => new HashSet<PermissionScope>(values);
}
