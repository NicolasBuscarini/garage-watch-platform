using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class ClipArchiveTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Worker_archives_ready_synthetic_clip_and_keeps_local_source_unchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-archive-tests", Guid.NewGuid().ToString("N"));
        var spool = Path.Combine(root, "spool");
        var destination = Path.Combine(root, "isolated-archive");
        var capture = Capture(spool);
        var clip = await WriteReadyClipAsync(spool);
        var worker = CreateWorker(capture, destination);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            var archived = await WaitForClipAsync(spool, clip.ClipId, value => value.ArchiveStatus == "archived");
            Assert.Equal("ready", archived.State);
            Assert.Equal(1, archived.ArchiveAttemptCount);
            Assert.NotNull(archived.ArchivedAt);
            Assert.Matches("^[a-f0-9]{64}$", archived.ArchiveManifestSha256!);

            var archiveDirectory = Path.Combine(destination, clip.CameraId, clip.ClipId);
            Assert.Equal(clip.MediaSha256, await HashAsync(Path.Combine(archiveDirectory, clip.MediaFileName!)));
            Assert.Equal(clip.ThumbnailSha256, await HashAsync(Path.Combine(archiveDirectory, clip.ThumbnailFileName!)));
            var archiveManifestPath = Path.Combine(archiveDirectory, "archive-manifest.json");
            Assert.True(File.Exists(archiveManifestPath));
            using var archivedMetadata = JsonDocument.Parse(await File.ReadAllTextAsync(archiveManifestPath));
            AssertArchiveManifestContract(archivedMetadata);
            Assert.Equal(clip.MediaSha256, await HashAsync(Path.Combine(spool, "clips", "media", clip.MediaFileName!)));
            Assert.Equal(clip.ThumbnailSha256, await HashAsync(Path.Combine(spool, "clips", "thumbnails", clip.ThumbnailFileName!)));
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await worker.StopAsync(timeout.Token);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Worker_reconciles_commit_after_crash_before_source_manifest_transition()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-archive-recovery", Guid.NewGuid().ToString("N"));
        var spool = Path.Combine(root, "spool");
        var destination = Path.Combine(root, "isolated-archive");
        var capture = Capture(spool);
        var clip = (await WriteReadyClipAsync(spool)) with { ArchiveStatus = "copying", ArchiveAttemptCount = 1 };
        var options = Options(destination);
        await new LocalFilesystemClipArchiveAdapter(options).ArchiveAsync(clip,
            Path.Combine(spool, "clips", "media", clip.MediaFileName!),
            Path.Combine(spool, "clips", "thumbnails", clip.ThumbnailFileName!), CancellationToken.None);
        await WriteClipManifestAsync(spool, clip);
        var worker = CreateWorker(capture, destination);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            var archived = await WaitForClipAsync(spool, clip.ClipId, value => value.ArchiveStatus == "archived");
            Assert.Equal(2, archived.ArchiveAttemptCount);
            Assert.Equal(clip.MediaSha256, archived.MediaSha256);
            Assert.Single(Directory.GetFiles(Path.Combine(destination, clip.CameraId, clip.ClipId), "*.ts"));
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await worker.StopAsync(timeout.Token);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Worker_fails_checksum_mismatch_without_deleting_or_mutating_source_media()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-archive-integrity", Guid.NewGuid().ToString("N"));
        var spool = Path.Combine(root, "spool");
        var destination = Path.Combine(root, "isolated-archive");
        var capture = Capture(spool);
        var clip = (await WriteReadyClipAsync(spool)) with { MediaSha256 = new string('0', 64) };
        await WriteClipManifestAsync(spool, clip);
        var original = await File.ReadAllBytesAsync(Path.Combine(spool, "clips", "media", clip.MediaFileName!));
        var worker = CreateWorker(capture, destination);
        try
        {
            await worker.StartAsync(CancellationToken.None);
            var failed = await WaitForClipAsync(spool, clip.ClipId, value => value.ArchiveStatus == "failed");
            Assert.Equal("ready", failed.State);
            Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(spool, "clips", "media", clip.MediaFileName!)));
            Assert.False(Directory.Exists(Path.Combine(destination, clip.CameraId, clip.ClipId))
                && File.Exists(Path.Combine(destination, clip.CameraId, clip.ClipId, "archive-manifest.json")));
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await worker.StopAsync(timeout.Token);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Archive_capacity_sets_retry_wait_and_resumes_after_capacity_is_increased()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-archive-capacity", Guid.NewGuid().ToString("N"));
        var spool = Path.Combine(root, "spool");
        var destination = Path.Combine(root, "isolated-archive");
        var capture = Capture(spool);
        var clip = await WriteReadyClipAsync(spool);
        var limited = new ClipArchiveOptions(destination, 1, 1, 1, 1_048_576);
        var first = CreateWorker(capture, limited);
        try
        {
            await first.StartAsync(CancellationToken.None);
            var waiting = await WaitForClipAsync(spool, clip.ClipId, value => value.ArchiveStatus == "retry_wait");
            Assert.Equal(1, waiting.ArchiveAttemptCount);
            Assert.NotNull(waiting.ArchiveNextAttemptAt);
            await first.StopAsync(CancellationToken.None);

            var expanded = CreateWorker(capture, Options(destination));
            try
            {
                await expanded.StartAsync(CancellationToken.None);
                var archived = await WaitForClipAsync(spool, clip.ClipId, value => value.ArchiveStatus == "archived");
                Assert.Equal(2, archived.ArchiveAttemptCount);
            }
            finally
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await expanded.StopAsync(timeout.Token);
            }
        }
        finally
        {
            if (!first.ExecuteTask!.IsCompleted)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await first.StopAsync(timeout.Token);
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Archive_configuration_rejects_destination_inside_or_around_spool()
    {
        var capture = Capture(Path.Combine(Path.GetTempPath(), "archive-config", "spool"));
        foreach (var path in new[] { Path.Combine(capture.SpoolDirectory, "archive"), Directory.GetParent(capture.SpoolDirectory)!.FullName })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["CLIP_ARCHIVE_DIRECTORY"] = path }).Build();
            Action configure = () => ClipArchiveOptions.FromConfiguration(config, capture);
            Assert.Throws<InvalidOperationException>(configure);
        }
    }

    [Fact]
    public async Task Archive_adapter_refuses_symlink_destination_without_writing_through_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-archive-symlink", Guid.NewGuid().ToString("N"));
        var spool = Path.Combine(root, "spool");
        var destinationTarget = Path.Combine(root, "outside-destination");
        var destinationLink = Path.Combine(root, "archive-link");
        var clip = await WriteReadyClipAsync(spool);
        Directory.CreateDirectory(destinationTarget);
        Directory.CreateSymbolicLink(destinationLink, destinationTarget);
        try
        {
            var adapter = new LocalFilesystemClipArchiveAdapter(Options(destinationLink));
            await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ArchiveAsync(clip,
                Path.Combine(spool, "clips", "media", clip.MediaFileName!),
                Path.Combine(spool, "clips", "thumbnails", clip.ThumbnailFileName!), CancellationToken.None));
            Assert.Empty(Directory.EnumerateFileSystemEntries(destinationTarget));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static ClipArchiveWorker CreateWorker(CaptureOptions capture, string destination) =>
        CreateWorker(capture, Options(destination));

    private static ClipArchiveWorker CreateWorker(CaptureOptions capture, ClipArchiveOptions options) =>
        new(capture, options, new LocalFilesystemClipArchiveAdapter(options),
            new CapacityManagerAlwaysAvailable(), NullLogger<ClipArchiveWorker>.Instance);

    private sealed class CapacityManagerAlwaysAvailable : IClipArchiveCapacityManager
    {
        public Task<bool> EnsureCapacityAsync(string cameraId, long incomingBytes,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private static ClipArchiveOptions Options(string destination) => new(destination, 1, 10, 10_000_000, 2_000_000);

    private static CaptureOptions Capture(string spool) => new("synthetic", "camera-archive", spool,
        1, 10, 1_048_576, null, "ffmpeg", "ffprobe");

    private static async Task<ClipManifestV1> WriteReadyClipAsync(string spool)
    {
        var clipId = Guid.NewGuid().ToString("N");
        var mediaName = clipId + ".ts";
        var thumbnailName = clipId + ".jpg";
        var mediaDirectory = Path.Combine(spool, "clips", "media");
        var thumbnailDirectory = Path.Combine(spool, "clips", "thumbnails");
        Directory.CreateDirectory(mediaDirectory);
        Directory.CreateDirectory(thumbnailDirectory);
        var media = System.Text.Encoding.UTF8.GetBytes("synthetic-clip-media-" + clipId);
        var thumbnail = System.Text.Encoding.UTF8.GetBytes("synthetic-thumbnail-" + clipId);
        await File.WriteAllBytesAsync(Path.Combine(mediaDirectory, mediaName), media);
        await File.WriteAllBytesAsync(Path.Combine(thumbnailDirectory, thumbnailName), thumbnail);
        var now = DateTimeOffset.UtcNow;
        var clip = new ClipManifestV1("1.0", clipId, "camera-archive", now.AddSeconds(-10), now,
            now.AddSeconds(-9), now.AddSeconds(-1), "ready", "fixture-trigger-v1", "synthetic:test",
            [Guid.NewGuid().ToString("N")], mediaName, Sha256(media), thumbnailName, Sha256(thumbnail), 1, now,
            PublicId: new PublicIdGenerator().Generate(PublicIdPrefixes.ClipPrefix));
        await WriteClipManifestAsync(spool, clip);
        return clip;
    }

    private static async Task WriteClipManifestAsync(string spool, ClipManifestV1 clip)
    {
        var directory = Path.Combine(spool, "clips", "manifests");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, clip.ClipId + ".json"),
            JsonSerializer.Serialize(clip, JsonOptions));
    }

    private static async Task<ClipManifestV1> WaitForClipAsync(string spool, string clipId,
        Func<ClipManifestV1, bool> predicate)
    {
        var path = Path.Combine(spool, "clips", "manifests", clipId + ".json");
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    var clip = JsonSerializer.Deserialize<ClipManifestV1>(await File.ReadAllTextAsync(path), JsonOptions);
                    if (clip is not null && predicate(clip)) return clip;
                }
                catch (JsonException) { }
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("archive_clip_transition_timeout");
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void AssertArchiveManifestContract(JsonDocument payload)
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts",
            "archived-clip-manifest.v1.schema.json")));
        var required = schema.RootElement.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        var properties = schema.RootElement.GetProperty("properties").EnumerateObject()
            .Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        var actual = payload.RootElement.EnumerateObject().Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
        Assert.True(required.SetEquals(properties));
        Assert.True(required.SetEquals(actual));
        Assert.Equal("1.0", payload.RootElement.GetProperty("schemaVersion").GetString());
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
}
