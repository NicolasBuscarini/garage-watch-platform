using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class ClipLibraryApiTests
{
    [Fact]
    public async Task Clip_timeline_filters_paginates_and_streams_only_authorized_integral_media_with_ranges()
    {
        await using var factory = new ApiFactory();
        var internalCameraId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        var cameraId = await GetCameraPublicIdAsync(factory, internalCameraId);
        var isolatedStart = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clip = await PersistClipAsync(factory, internalCameraId, isolatedStart.AddDays(-1));
        var other = await PersistClipAsync(factory, internalCameraId, isolatedStart);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/clips")).StatusCode);

        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        using var page = await admin.GetAsync($"/api/v1/clips?cameraId={cameraId}&fromUtc={Uri.EscapeDataString(isolatedStart.AddDays(-2).ToString("O"))}&toUtc={Uri.EscapeDataString(isolatedStart.AddDays(2).ToString("O"))}&page=1&pageSize=1&state=ready");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var result = await page.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, result.GetProperty("total").GetInt32());
        Assert.Equal(other.PublicId, result.GetProperty("items")[0].GetProperty("publicId").GetString());
        Assert.False(result.GetProperty("items")[0].TryGetProperty("clipId", out _));
        Assert.Equal(cameraId, result.GetProperty("items")[0].GetProperty("cameraId").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.GetAsync($"/api/v1/clips?cameraId={internalCameraId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.GetAsync($"/api/v1/clips/{clip.ClipId}")).StatusCode);

        using var filtered = await admin.GetAsync($"/api/v1/clips?cameraId={cameraId}&fromUtc={Uri.EscapeDataString(isolatedStart.AddHours(-1).ToString("O"))}");
        var filteredBody = await filtered.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, filteredBody.GetProperty("total").GetInt32());
        Assert.Equal(other.PublicId, filteredBody.GetProperty("items")[0].GetProperty("publicId").GetString());

        using var detail = await admin.GetAsync($"/api/v1/clips/{clip.PublicId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var thumbnail = await admin.GetAsync($"/api/v1/clips/{clip.PublicId}/thumbnail");
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        admin.DefaultRequestHeaders.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 63);
        using var media = await admin.GetAsync($"/api/v1/clips/{clip.PublicId}/media");
        Assert.Equal(HttpStatusCode.PartialContent, media.StatusCode);
        Assert.Equal("video/mp4", media.Content.Headers.ContentType?.MediaType);
        var head = await media.Content.ReadAsByteArrayAsync();
        Assert.True(head.Length >= 12);
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(head, 4, 4));

        using var corrupt = await admin.GetAsync($"/api/v1/clips/{clip.PublicId}/media");
        Assert.Equal(HttpStatusCode.PartialContent, corrupt.StatusCode); // The original remains intact.
        var paths = factory.Services.GetRequiredService<CaptureOptions>();
        await File.WriteAllBytesAsync(Path.Combine(paths.SpoolDirectory, "clips", "media", clip.MediaFileName!), [0x00]);
        using var tampered = await admin.GetAsync($"/api/v1/clips/{clip.PublicId}/media");
        Assert.Equal(HttpStatusCode.NotFound, tampered.StatusCode);
        using var badQuery = await admin.GetAsync($"/api/v1/clips?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, badQuery.StatusCode);
    }

    [Fact]
    public async Task User_without_clips_read_cannot_enumerate_or_fetch_clip_media()
    {
        await using var factory = new ApiFactory();
        var internalCameraId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        var cameraId = await GetCameraPublicIdAsync(factory, internalCameraId);
        var clip = await PersistClipAsync(factory, internalCameraId, DateTimeOffset.UtcNow);
        var adminId = await GetAdminIdAsync(factory);
        Guid userId;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "clips-denied", IsActive = true };
            Assert.True((await users.CreateAsync(user, "Clips-Denied-Password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "User")).Succeeded);
            userId = user.Id;
            var profiles = scope.ServiceProvider.GetRequiredService<AccessProfileService>();
            var videoOnly = await profiles.CreateAsync(adminId, "Video without clips", ["live.video.view"], CancellationToken.None);
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var versionId = await database.AccessProfileVersions.AsNoTracking()
                .Where(item => item.PublicId == videoOnly.CurrentVersionPublicId).Select(item => item.Id).SingleAsync();
            await profiles.ReplaceAssignmentsAsync(adminId, userId,
                [new ProfileAssignmentRequest(versionId, [new("garage", internalCameraId)])], CancellationToken.None);
        }

        using var client = await LoginAsync(factory, "clips-denied", "Clips-Denied-Password-123!");
        using var list = await client.GetAsync($"/api/v1/clips?cameraId={cameraId}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(0, (await list.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/clips/{clip.PublicId}/media")).StatusCode);
    }

    private static async Task<Guid> GetAdminIdAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByNameAsync("contract-user"))!.Id;
    }

    private static async Task<string> GetCameraPublicIdAsync(ApiFactory factory, string internalCameraId)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IamDbContext>().CameraCatalog.AsNoTracking()
            .Where(item => item.CameraId == internalCameraId).Select(item => item.PublicId).SingleAsync())!;
    }

    private static async Task<ClipManifestV1> PersistClipAsync(ApiFactory factory, string cameraId, DateTimeOffset start)
    {
        var capture = factory.Services.GetRequiredService<CaptureOptions>();
        var id = Guid.NewGuid().ToString("N");
        var root = Path.Combine(capture.SpoolDirectory, "clips");
        var mediaDir = Path.Combine(root, "media");
        var thumbsDir = Path.Combine(root, "thumbnails");
        var manifests = Path.Combine(root, "manifests");
        Directory.CreateDirectory(mediaDir);
        Directory.CreateDirectory(thumbsDir);
        Directory.CreateDirectory(manifests);
        var mediaName = id + ".ts";
        var thumbnailName = id + ".jpg";
        await RunFfmpegAsync(Path.Combine(mediaDir, mediaName));
        await File.WriteAllBytesAsync(Path.Combine(thumbsDir, thumbnailName), [0xff, 0xd8, 0xff, 0xd9]);
        var mediaHash = await HashAsync(Path.Combine(mediaDir, mediaName));
        var thumbnailHash = await HashAsync(Path.Combine(thumbsDir, thumbnailName));
        var clip = new ClipManifestV1("1.0", id, cameraId, start, start.AddMinutes(1), start, start.AddMinutes(1),
            "ready", "trigger-v1", "visual_change:contract", ["segment-contract"], mediaName, mediaHash,
            thumbnailName, thumbnailHash, 1, start.AddMinutes(1), PublicId: new PublicIdGenerator().Generate(PublicIdPrefixes.ClipPrefix));
        await File.WriteAllTextAsync(Path.Combine(manifests, id + ".json"),
            JsonSerializer.Serialize(clip, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return clip;
    }

    private static async Task RunFfmpegAsync(string destination)
    {
        var start = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                     "color=c=black:s=32x24:r=5", "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", "-threads", "1",
                     "-pix_fmt", "yuv420p", "-f", "mpegts", destination })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("ffmpeg_start_failed");
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await stderr;
        Assert.True(process.ExitCode == 0, error);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
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
