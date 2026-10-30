using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Application.Cameras;
using GarageWatch.Application.Identity;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class CameraCatalogTests
{
    [Fact]
    public async Task Bootstrap_seeds_the_configured_camera_and_keeps_catalog_state_persistent()
    {
        var path = Path.Combine(Path.GetTempPath(), "garagewatch-tests", $"{Guid.NewGuid():N}.db");
        await using (var factory = new ApiFactory(databasePath: path))
        {
            using var scope = factory.Services.CreateScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ICameraCatalog>();
            var contexts = await catalog.ListContextsAsync(1, 20);
            var cameras = await catalog.ListCamerasAsync(1, 20);

            var context = Assert.Single(contexts.Items);
            Assert.Equal("garage", context.ContextId);
            Assert.True(context.IsEnabled);
            var camera = Assert.Single(cameras.Items);
            Assert.Equal(scope.ServiceProvider.GetRequiredService<CaptureOptions>().CameraId, camera.CameraId);
            Assert.Equal("garage", camera.ContextId);
            Assert.True(camera.IsEnabled);
        }

        await using (var restarted = new ApiFactory(databasePath: path, bootstrapUsername: null))
        using (var scope = restarted.Services.CreateScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<ICameraCatalog>();
            Assert.Single((await catalog.ListCamerasAsync(1, 20)).Items);
        }
    }

    [Fact]
    public async Task Admin_crud_is_versioned_audited_and_deactivation_is_logical()
    {
        await using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var administrator = (await users.FindByNameAsync("contract-user"))!;
        var catalog = scope.ServiceProvider.GetRequiredService<ICameraCatalog>();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();

        var context = await catalog.CreateContextAsync(administrator.Id, "shed", "Oficina");
        var camera = await catalog.CreateCameraAsync(administrator.Id, "shed-cam-01", context.ContextId, "Porta lateral");
        Assert.Equal(1, camera.Version);

        var renamed = await catalog.UpdateCameraAsync(administrator.Id, camera.CameraId, camera.Version,
            camera.ContextId, "Porta dos fundos", true);
        Assert.Equal(2, renamed.Version);
        await Assert.ThrowsAsync<CatalogConflictException>(() => catalog.UpdateCameraAsync(administrator.Id,
            camera.CameraId, camera.Version, camera.ContextId, "Stale write", true));

        await Assert.ThrowsAsync<CatalogConflictException>(() => catalog.DeactivateContextAsync(administrator.Id,
            context.ContextId, context.Version));
        var inactiveCamera = await catalog.DeactivateCameraAsync(administrator.Id, camera.CameraId, renamed.Version);
        var inactiveContext = await catalog.DeactivateContextAsync(administrator.Id, context.ContextId, context.Version);

        Assert.False(inactiveCamera.IsEnabled);
        Assert.False(inactiveContext.IsEnabled);
        Assert.Equal(2, await database.CameraCatalog.CountAsync());
        Assert.Equal(5, await database.IamAuditEvents.CountAsync(entry =>
            entry.Action.StartsWith("camera_catalog.") || entry.Action.StartsWith("camera_context.")));
        Assert.Equal(6, await database.IamAuditEvents.CountAsync());
        Assert.Contains(await database.IamAuditEvents.Select(entry => entry.Action).ToArrayAsync(),
            action => action == "camera_catalog.deactivated");
    }

    [Fact]
    public async Task Camera_collection_shows_admin_all_items_and_viewers_only_active_grants()
    {
        await using var factory = new ApiFactory();
        using var adminClient = factory.CreateClient();
        using var login = await adminClient.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        adminClient.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);

        Guid viewerId;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var viewer = new ApplicationUser { Id = Guid.NewGuid(), UserName = "catalog-viewer", IsActive = true };
            Assert.True((await users.CreateAsync(viewer, "Viewer-Password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(viewer, "Viewer")).Succeeded);
            viewerId = viewer.Id;
            var admin = (await users.FindByNameAsync("contract-user"))!;
            var cameraId = scope.ServiceProvider.GetRequiredService<CaptureOptions>().CameraId;
            var catalog = scope.ServiceProvider.GetRequiredService<ICameraCatalog>();
            await catalog.CreateCameraAsync(admin.Id, "ungranted-camera", "garage", "Sem concessão");
        }
        Guid adminIdForGrant;
        using (var adminScope = factory.Services.CreateScope())
            adminIdForGrant = (await adminScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
                .FindByNameAsync("contract-user"))!.Id;
        await AccessProfileTestHelper.AssignVideoAsync(factory.Services, adminIdForGrant, viewerId, "garage",
            factory.Services.GetRequiredService<CaptureOptions>().CameraId);

        using var adminResponse = await adminClient.GetAsync("/api/v1/cameras");
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
        using var adminJson = JsonDocument.Parse(await adminResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, adminJson.RootElement.GetArrayLength());

        using var scope2 = factory.Services.CreateScope();
        var viewerUser = await scope2.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByIdAsync(viewerId.ToString());
        var storedGrant = await scope2.ServiceProvider.GetRequiredService<IamDbContext>().UserAccessProfileScopes
            .AsNoTracking().SingleAsync();
        Assert.Equal("garage", storedGrant.ContextId);
        Assert.Equal(scope2.ServiceProvider.GetRequiredService<CaptureOptions>().CameraId, storedGrant.CameraId);
        var catalogDb = scope2.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.Equal(1, await catalogDb.CameraCatalog.CountAsync(item => item.CameraId == storedGrant.CameraId && item.IsEnabled));
        Assert.True(await catalogDb.CameraContexts.AnyAsync(item => item.ContextId == "garage" && item.IsEnabled));
        Assert.Equal(1, await catalogDb.UserAccessProfileScopes.CountAsync(item => item.CameraId == storedGrant.CameraId && item.ContextId == "garage"));
        var visible = await scope2.ServiceProvider.GetRequiredService<ICameraCatalog>()
            .ListVisibleCamerasAsync(viewerUser!.Id, false);
        Assert.Equal(storedGrant.CameraId, Assert.Single(visible).CameraId);

        using var apiViewer = factory.CreateClient();
        using var viewerLogin = await apiViewer.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "catalog-viewer", password = "Viewer-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, viewerLogin.StatusCode);
        apiViewer.DefaultRequestHeaders.Add("Cookie", Assert.Single(viewerLogin.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        using var viewerResponse = await apiViewer.GetAsync("/api/v1/cameras");
        using var viewerJson = JsonDocument.Parse(await viewerResponse.Content.ReadAsStringAsync());
        Assert.Equal(storedGrant.CameraId, viewerJson.RootElement[0].GetProperty("cameraId").GetString());
    }
}
