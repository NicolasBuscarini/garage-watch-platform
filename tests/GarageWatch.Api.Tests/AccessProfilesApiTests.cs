using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class AccessProfilesApiTests
{
    [Fact]
    public async Task Startup_migrates_legacy_camera_grants_once_to_the_least_privilege_profile()
    {
        await using var factory = new ApiFactory();
        Guid adminId;
        Guid viewerId;
        string cameraId;
        using (var scope = factory.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            adminId = (await users.FindByNameAsync("contract-user"))!.Id;
            cameraId = services.GetRequiredService<CaptureOptions>().CameraId;
            var viewer = new ApplicationUser { Id = Guid.NewGuid(), UserName = "legacy-viewer", IsActive = true };
            Assert.True((await users.CreateAsync(viewer, "Legacy-Password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(viewer, "Viewer")).Succeeded);
            viewerId = viewer.Id;
            await services.GetRequiredService<IdentityAdministrationService>()
                .GrantCameraAsync(adminId, viewerId, "garage", cameraId);

            var database = services.GetRequiredService<IamDbContext>();
            database.IamMigrationStates.RemoveRange(database.IamMigrationStates);
            await database.SaveChangesAsync();
            await services.GetRequiredService<IamDatabaseInitializer>().InitializeAsync();
            await services.GetRequiredService<IamDatabaseInitializer>().InitializeAsync();
        }

        using var validationScope = factory.Services.CreateScope();
        var db = validationScope.ServiceProvider.GetRequiredService<IamDbContext>();
        var assignment = await db.UserAccessProfileAssignments.Include(item => item.ProfileVersion)
            .ThenInclude(version => version!.Permissions).Include(item => item.Scopes)
            .SingleAsync(item => item.UserId == viewerId && item.RevokedAtUtc == null);
        Assert.Equal("Live somente vídeo", (await db.AccessProfiles.FindAsync(assignment.ProfileVersion!.ProfileId))!.Name);
        Assert.Equal(new[] { "live.video.view" }, assignment.ProfileVersion.Permissions
            .Select(permission => permission.PermissionKey).ToArray());
        var assignedScope = Assert.Single(assignment.Scopes);
        Assert.Equal("garage", assignedScope.ContextId);
        Assert.Equal(cameraId, assignedScope.CameraId);
        Assert.Equal(1, await db.UserAccessProfileAssignments.CountAsync(item =>
            item.UserId == viewerId && item.RevokedAtUtc == null));
    }

    [Fact]
    public async Task Profiles_are_versioned_and_users_receive_only_profile_assignments_with_explicit_camera_scope()
    {
        await using var factory = new ApiFactory();
        Guid adminId;
        Guid userId;
        string cameraId;
        using (var scope = factory.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            adminId = (await users.FindByNameAsync("contract-user"))!.Id;
            cameraId = services.GetRequiredService<CaptureOptions>().CameraId;
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "profile-user", IsActive = true };
            Assert.True((await users.CreateAsync(user, "Profile-Password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "User")).Succeeded);
            userId = user.Id;
        }

        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var catalogResponse = await admin.GetAsync("/api/v1/admin/permission-catalog");
        catalogResponse.EnsureSuccessStatusCode();
        var catalog = await catalogResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(catalog.EnumerateArray(), item => item.GetProperty("key").GetString() == "live.video.view");
        Assert.DoesNotContain(catalog.EnumerateArray(), item => item.GetProperty("key").GetString() == "admin.users.manage");

        using var forbiddenPermission = await admin.PostAsJsonAsync("/api/v1/admin/access-rules",
            new { name = "Unsafe", permissionKeys = new[] { "admin.users.manage" } });
        Assert.Equal(HttpStatusCode.BadRequest, forbiddenPermission.StatusCode);
        using var create = await admin.PostAsJsonAsync("/api/v1/admin/access-rules",
            new { name = "Garage operator", permissionKeys = new[] { "camera.list", "live.video.view" } });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var profileVersionId = created.GetProperty("currentVersionId").GetGuid();
        var profileId = created.GetProperty("id").GetGuid();

        using var invalidScope = await admin.PutAsJsonAsync($"/api/v1/admin/users/{userId}/access-rules", new
        {
            assignments = new[]
            {
                new { profileVersionId, scopes = new[] { new { contextId = "wrong-context", cameraId } } }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidScope.StatusCode);

        using var assign = await admin.PutAsJsonAsync($"/api/v1/admin/users/{userId}/access-rules", new
        {
            assignments = new[]
            {
                new { profileVersionId, scopes = new[] { new { contextId = "garage", cameraId } } }
            }
        });
        Assert.Equal(HttpStatusCode.NoContent, assign.StatusCode);

        using var detail = await admin.GetAsync($"/api/v1/admin/users/{userId}");
        var detailJson = await detail.Content.ReadFromJsonAsync<JsonElement>();
        var assignedVersion = detailJson.GetProperty("accessProfiles")[0].GetProperty("profileVersionId").GetGuid();
        Assert.Equal(profileVersionId, assignedVersion);

        using var profileUser = await LoginAsync(factory, "profile-user", "Profile-Password-123!");
        var me = await profileUser.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal("User", me.GetProperty("role").GetString());
        Assert.True(me.GetProperty("canMonitor").GetBoolean());
        using var cameras = await profileUser.GetAsync("/api/v1/cameras");
        cameras.EnsureSuccessStatusCode();
        var cameraList = await cameras.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(cameraList.EnumerateArray(), item => item.GetProperty("cameraId").GetString() == cameraId);

        using var publish = await admin.PostAsJsonAsync($"/api/v1/admin/access-rules/{profileId}/versions", new
        {
            expectedVersion = 1,
            permissionKeys = new[] { "live.video.view", "events.read" }
        });
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var published = await publish.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, published.GetProperty("version").GetInt64());
        using var unchangedDetail = await admin.GetAsync($"/api/v1/admin/users/{userId}");
        var stillPinned = await unchangedDetail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(profileVersionId,
            stillPinned.GetProperty("accessProfiles")[0].GetProperty("profileVersionId").GetGuid());

        using var adminAssignment = await admin.PutAsJsonAsync($"/api/v1/admin/users/{adminId}/access-rules", new
        {
            assignments = new[]
            {
                new { profileVersionId, scopes = new[] { new { contextId = "garage", cameraId } } }
            }
        });
        Assert.Equal(HttpStatusCode.Conflict, adminAssignment.StatusCode);

        using var revoke = await admin.PutAsJsonAsync($"/api/v1/admin/users/{userId}/access-rules",
            new { assignments = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await profileUser.GetAsync("/api/v1/auth/me")).StatusCode);
        using var reauthenticated = await LoginAsync(factory, "profile-user", "Profile-Password-123!");
        var afterRevoke = await reauthenticated.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.False(afterRevoke.GetProperty("canMonitor").GetBoolean());
        using var deniedMedia = await reauthenticated.GetAsync($"/api/v1/live/{cameraId}/index.m3u8");
        Assert.Equal(HttpStatusCode.NotFound, deniedMedia.StatusCode);
    }

    private static async Task<HttpClient> LoginAsync(ApiFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        return client;
    }
}
