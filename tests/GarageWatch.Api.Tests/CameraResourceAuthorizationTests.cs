using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Application.Identity;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class CameraResourceAuthorizationTests
{
    [Fact]
    public async Task Live_playlist_segments_and_ranges_are_scoped_to_assigned_profile_and_camera_pair()
    {
        await using var factory = new ApiFactory();
        var cameraId = "capture-test";
        Guid adminId;
        Guid grantedViewerId;
        Guid ungrantedViewerId;

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            adminId = (await users.FindByNameAsync("contract-user"))!.Id;
            cameraId = scope.ServiceProvider.GetRequiredService<CaptureOptions>().CameraId;
            grantedViewerId = await CreateViewerAsync(users, "granted-viewer");
            ungrantedViewerId = await CreateViewerAsync(users, "ungranted-viewer");
            await CreateUserAsync(users, "ordinary-user");
        }
        await AccessProfileTestHelper.AssignVideoAsync(factory.Services, adminId, grantedViewerId, "garage", cameraId);

        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        using var granted = await LoginAsync(factory, "granted-viewer", "Viewer-Password-123!");
        using var ungranted = await LoginAsync(factory, "ungranted-viewer", "Viewer-Password-123!");
        using var ordinary = await LoginAsync(factory, "ordinary-user", "Viewer-Password-123!");
        Assert.Equal(HttpStatusCode.OK, (await ordinary.GetAsync("/api/v1/cameras")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ordinary.GetAsync($"/api/v1/live/{cameraId}/index.m3u8")).StatusCode);

        HttpResponseMessage? playlist = null;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            playlist?.Dispose();
            playlist = await granted.GetAsync($"/api/v1/live/{cameraId}/index.m3u8");
            if (playlist.StatusCode == HttpStatusCode.OK) break;
            await Task.Delay(50);
        }

        Assert.NotNull(playlist);
        using (playlist)
        {
            Assert.Equal(HttpStatusCode.OK, playlist.StatusCode);
            var playlistText = await playlist.Content.ReadAsStringAsync();
            var segmentPath = playlistText.Split('\n')
                .First(line => line.StartsWith("/api/v1/live/", StringComparison.Ordinal)).Trim();

            using var authorizedRange = new HttpRequestMessage(HttpMethod.Get, segmentPath);
            authorizedRange.Headers.Range = new RangeHeaderValue(0, 9);
            using var rangeResponse = await granted.SendAsync(authorizedRange);
            Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
            Assert.Equal(10, (await rangeResponse.Content.ReadAsByteArrayAsync()).Length);

            Assert.Equal(HttpStatusCode.NotFound,
                (await ungranted.GetAsync($"/api/v1/live/{cameraId}/index.m3u8")).StatusCode);
            using var deniedRange = new HttpRequestMessage(HttpMethod.Get, segmentPath);
            deniedRange.Headers.Range = new RangeHeaderValue(0, 9);
            using var deniedSegment = await ungranted.SendAsync(deniedRange);
            Assert.Equal(HttpStatusCode.NotFound, deniedSegment.StatusCode);

            using var wrongCameraSegment = await granted.GetAsync(segmentPath.Replace(cameraId, "forged-camera",
                StringComparison.Ordinal));
            Assert.Equal(HttpStatusCode.NotFound, wrongCameraSegment.StatusCode);

            using var cameras = await ungranted.GetAsync("/api/v1/cameras");
            Assert.Equal(HttpStatusCode.OK, cameras.StatusCode);
            Assert.Equal(0, (await cameras.Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());

            using var adminPlaylist = await admin.GetAsync($"/api/v1/live/{cameraId}/index.m3u8");
            Assert.Equal(HttpStatusCode.OK, adminPlaylist.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var profiles = scope.ServiceProvider.GetRequiredService<AccessProfileService>();
            await profiles.ReplaceAssignmentsAsync(adminId, grantedViewerId, [], CancellationToken.None);
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            Assert.True(await database.IamAuditEvents.AnyAsync(entry => entry.SubjectUserId == ungrantedViewerId &&
                entry.Action == "camera.access.denied" && entry.ResourceId == cameraId));
        }

        // A still-open cookie cannot use a grant removed from the authoritative SQLite store.
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await granted.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await granted.GetAsync($"/api/v1/live/{cameraId}/segments/not-a-real-segment")).StatusCode);
    }

    private static async Task<Guid> CreateViewerAsync(UserManager<ApplicationUser> users, string username)
    {
        var viewer = new ApplicationUser { Id = Guid.NewGuid(), UserName = username, IsActive = true };
        Assert.True((await users.CreateAsync(viewer, "Viewer-Password-123!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(viewer, "Viewer")).Succeeded);
        return viewer.Id;
    }

    private static async Task CreateUserAsync(UserManager<ApplicationUser> users, string username)
    {
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = username, IsActive = true };
        Assert.True((await users.CreateAsync(user, "Viewer-Password-123!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, "User")).Succeeded);
    }

    private static async Task<HttpClient> LoginAsync(ApiFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        return client;
    }
}
