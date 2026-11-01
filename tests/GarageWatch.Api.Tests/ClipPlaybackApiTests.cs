using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class ClipPlaybackApiTests
{
    [Fact]
    public async Task Media_endpoint_serves_derived_mp4_without_audio_and_reuses_the_cache()
    {
        await using var factory = new ApiFactory();
        var capture = factory.Services.GetRequiredService<CaptureOptions>();
        var clip = await PersistClipAsync(factory, capture.CameraId, withAudio: true);
        using var admin = await LoginAsync(factory);

        using var first = await admin.GetAsync($"/api/v1/clips/{clip.PublicId}/media");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("video/mp4", first.Content.Headers.ContentType?.MediaType);
        var bytes = await first.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 0);
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(bytes, 4, 4));

        var derived = Path.Combine(capture.SpoolDirectory, "clips", "playback");
        var cached = Path.Combine(derived, $"{clip.ClipId}-{clip.MediaSha256![..12]}.mp4");
        Assert.True(File.Exists(cached), cached);
        Assert.Equal(["video"], await ProbeStreamKindsAsync(cached));
        var writtenAt = File.GetLastWriteTimeUtc(cached);

        using var second = await admin.GetAsync($"/api/v1/clips/{clip.PublicId}/media");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(cached));

        // The authoritative clip stays an untouched MPEG-TS file with its audio track.
        var source = Path.Combine(capture.SpoolDirectory, "clips", "media", clip.MediaFileName!);
        Assert.True(File.Exists(source));
        Assert.Equal(await HashAsync(source), clip.MediaSha256);
    }

    [Fact]
    public async Task Unplayable_source_fails_closed_without_leaving_a_derived_copy()
    {
        await using var factory = new ApiFactory();
        var capture = factory.Services.GetRequiredService<CaptureOptions>();
        var clip = await PersistClipAsync(factory, capture.CameraId, withAudio: false, validMedia: false);
        using var admin = await LoginAsync(factory);

        using var response = await admin.GetAsync($"/api/v1/clips/{clip.PublicId}/media");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("clip_playback_unavailable", await response.Content.ReadAsStringAsync());
        var derived = Path.Combine(capture.SpoolDirectory, "clips", "playback",
            $"{clip.ClipId}-{clip.MediaSha256![..12]}.mp4");
        Assert.False(File.Exists(derived), derived);
    }

    [Fact]
    public async Task Playback_cache_is_bounded_by_the_configured_size()
    {
        await using var factory = new ApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ClipPlaybackOptions>();
                services.AddSingleton(provider => new ClipPlaybackOptions(
                    Path.Combine(provider.GetRequiredService<CaptureOptions>().SpoolDirectory, "clips", "playback"),
                    1, 20));
            }));
        var capture = factory.Services.GetRequiredService<CaptureOptions>();
        var spool = capture.SpoolDirectory;
        var older = await PersistClipAsync(factory, capture.CameraId, withAudio: false);
        var newer = await PersistClipAsync(factory, capture.CameraId, withAudio: false);
        using var admin = await LoginAsync(factory);

        using var first = await admin.GetAsync($"/api/v1/clips/{older.PublicId}/media");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var second = await admin.GetAsync($"/api/v1/clips/{newer.PublicId}/media");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var playback = Path.Combine(spool, "clips", "playback");
        Assert.True(File.Exists(Path.Combine(playback, $"{newer.ClipId}-{newer.MediaSha256![..12]}.mp4")));
        Assert.False(File.Exists(Path.Combine(playback, $"{older.ClipId}-{older.MediaSha256![..12]}.mp4")));
    }

    private static async Task<string[]> ProbeStreamKindsAsync(string path)
    {
        var start = new System.Diagnostics.ProcessStartInfo("ffprobe")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-v", "error", "-show_entries", "stream=codec_type", "-of", "csv=p=0", path })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("ffprobe_start_failed");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stderr);
        return (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task<ClipManifestV1> PersistClipAsync(WebApplicationFactory<Program> factory, string cameraId,
        bool withAudio, bool validMedia = true)
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
        var mediaPath = Path.Combine(mediaDir, mediaName);
        if (validMedia) await RunFfmpegAsync(mediaPath, withAudio);
        else await File.WriteAllBytesAsync(mediaPath, [0x47, 0x01, 0x02, 0x03]);
        var thumbnailName = id + ".jpg";
        await File.WriteAllBytesAsync(Path.Combine(thumbsDir, thumbnailName), [0xff, 0xd8, 0xff, 0xd9]);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var clip = new ClipManifestV1("1.0", id, cameraId, start, start.AddMinutes(1), start, start.AddMinutes(1),
            "ready", "trigger-v1", "visual_change:contract", ["segment-contract"], mediaName,
            await HashAsync(mediaPath), thumbnailName,
            await HashAsync(Path.Combine(thumbsDir, thumbnailName)), 1, start.AddMinutes(1),
            PublicId: new PublicIdGenerator().Generate(PublicIdPrefixes.ClipPrefix));
        await File.WriteAllTextAsync(Path.Combine(manifests, id + ".json"),
            JsonSerializer.Serialize(clip, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return clip;
    }

    private static async Task RunFfmpegAsync(string destination, bool withAudio)
    {
        var start = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        var arguments = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=32x24:r=5"
        };
        if (withAudio) arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=8000"]);
        arguments.AddRange(["-t", "1", "-c:v", "libx264", "-preset", "ultrafast", "-threads", "1", "-pix_fmt", "yuv420p"]);
        if (withAudio) arguments.AddRange(["-c:a", "aac", "-shortest"]);
        arguments.AddRange(["-f", "mpegts", destination]);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
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

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        return client;
    }
}
