using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class ClipRetentionApiTests
{
    [Fact]
    public async Task Per_camera_policy_and_preservation_are_versioned_and_only_unprotected_archive_is_expired()
    {
        await using var factory = new ApiFactory();
        var capture = factory.Services.GetRequiredService<CaptureOptions>();
        var cameraId = await GetCameraPublicIdAsync(factory, capture.CameraId);
        var old = await WriteArchivedClipAsync(capture, capture.CameraId, DateTimeOffset.UtcNow.AddDays(-30));
        var protectedClip = await WriteArchivedClipAsync(capture, capture.CameraId, DateTimeOffset.UtcNow.AddDays(-31));
        var activeClip = await WriteArchivedClipAsync(capture, capture.CameraId, DateTimeOffset.UtcNow.AddDays(-32));
        using var admin = await LoginAsync(factory);
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var save = await admin.PutAsJsonAsync($"/api/v1/admin/cameras/{cameraId}/clip-retention",
            new { expectedVersion = 0, retentionDays = 15, maximumStorageGigabytes = 50 });
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var policy = await save.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(50, policy.GetProperty("maximumStorageGigabytes").GetInt32());
        Assert.Equal(1, policy.GetProperty("version").GetInt64());

        using var preserve = await admin.PutAsJsonAsync($"/api/v1/clips/{protectedClip.PublicId}/preservation",
            new { expectedVersion = 0, isPreserved = true });
        Assert.Equal(HttpStatusCode.OK, preserve.StatusCode);
        var preserved = await preserve.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(preserved.GetProperty("isPreserved").GetBoolean());

        var archive = Path.Combine(Path.GetTempPath(), "garagewatch-retention-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archive);
        await WriteArchiveCopyAsync(archive, capture, old);
        await WriteArchiveCopyAsync(archive, capture, protectedClip);
        await WriteArchiveCopyAsync(archive, capture, activeClip);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            database.ClipAnalysisJobs.Add(new ClipAnalysisJob
            {
                Id = Guid.NewGuid(),
                ClipId = activeClip.ClipId,
                ContextId = "garage",
                CameraId = capture.CameraId,
                IdempotencyKey = Guid.NewGuid(),
                AnalysisVersion = "contract-v1",
                PolicyVersion = 1,
                State = "processing",
                RequestKind = "manual",
                AttemptCount = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await database.SaveChangesAsync();
        }
        var worker = new ClipRetentionWorker(new ClipArchiveOptions(archive, 1, 100, long.MaxValue, long.MaxValue),
            capture, factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<ClipReadLeaseRegistry>(), TimeProvider.System,
            NullLogger<ClipRetentionWorker>.Instance);
        try
        {
            await worker.RunOnceAsync(CancellationToken.None);
            Assert.False(Directory.Exists(Path.Combine(archive, capture.CameraId, old.ClipId)));
            Assert.True(Directory.Exists(Path.Combine(archive, capture.CameraId, protectedClip.ClipId)));
            Assert.True(Directory.Exists(Path.Combine(archive, capture.CameraId, activeClip.ClipId)));
            Assert.True(File.Exists(Path.Combine(capture.SpoolDirectory, "clips", "media", old.MediaFileName!)));
            Assert.True(File.Exists(Path.Combine(capture.SpoolDirectory, "clips", "manifests", old.ClipId + ".json")));
            var expiredManifest = JsonSerializer.Deserialize<ClipManifestV1>(await File.ReadAllTextAsync(
                Path.Combine(capture.SpoolDirectory, "clips", "manifests", old.ClipId + ".json")),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal("expired", expiredManifest!.ArchiveStatus);
            using var list = await admin.GetAsync($"/api/v1/clips?cameraId={cameraId}");
            var page = await list.Content.ReadFromJsonAsync<JsonElement>();
            var retainedItem = page.GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("publicId").GetString() == protectedClip.PublicId);
            Assert.True(retainedItem.GetProperty("isPreserved").GetBoolean());
        }
        finally
        {
            Directory.Delete(archive, recursive: true);
        }
    }

    [Fact]
    public async Task Archive_worker_refuses_per_camera_quota_when_no_capacity_is_configured()
    {
        await using var factory = new ApiFactory();
        var capture = factory.Services.GetRequiredService<CaptureOptions>();
        var existing = await WriteArchivedClipAsync(capture, capture.CameraId, DateTimeOffset.UtcNow);
        var newClip = await WriteArchivedClipAsync(capture, capture.CameraId, DateTimeOffset.UtcNow.AddMinutes(1));
        newClip = newClip with { ArchiveStatus = "not_requested", ArchivedAt = null, ArchiveManifestSha256 = null };
        await File.WriteAllTextAsync(Path.Combine(capture.SpoolDirectory, "clips", "manifests", newClip.ClipId + ".json"),
            JsonSerializer.Serialize(newClip, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var archive = Path.Combine(Path.GetTempPath(), "garagewatch-retention-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archive);
        await WriteArchiveCopyAsync(archive, capture, existing);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            database.ClipRetentionPolicies.Add(new ClipRetentionPolicy
            {
                CameraId = capture.CameraId,
                RetentionDays = 3650,
                MaximumStorageBytes = 1,
                Version = 1,
                UpdatedByUserId = Guid.Empty.ToString("D"),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await database.SaveChangesAsync();
        }
        var options = new ClipArchiveOptions(archive, 1, 100, long.MaxValue, long.MaxValue);
        var capacityManager = new ClipRetentionWorker(options, capture,
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<ClipReadLeaseRegistry>(), TimeProvider.System,
            NullLogger<ClipRetentionWorker>.Instance);
        var worker = new ClipArchiveWorker(capture, options, new LocalFilesystemClipArchiveAdapter(options),
            capacityManager, NullLogger<ClipArchiveWorker>.Instance);
        try
        {
            await worker.RunOnceAsync(CancellationToken.None);
            var source = JsonSerializer.Deserialize<ClipManifestV1>(await File.ReadAllTextAsync(
                Path.Combine(capture.SpoolDirectory, "clips", "manifests", newClip.ClipId + ".json")),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal("retry_wait", source!.ArchiveStatus);
            Assert.False(Directory.Exists(Path.Combine(archive, capture.CameraId, newClip.ClipId)));
            Assert.True(Directory.Exists(Path.Combine(archive, capture.CameraId, existing.ClipId)));
        }
        finally
        {
            Directory.Delete(archive, recursive: true);
        }
    }

    [Fact]
    public async Task Archive_worker_replaces_oldest_eligible_copy_to_make_room_for_new_clip()
    {
        await using var factory = new ApiFactory();
        var capture = factory.Services.GetRequiredService<CaptureOptions>();
        var old = await WriteArchivedClipAsync(capture, capture.CameraId, DateTimeOffset.UtcNow.AddDays(-20));
        var incoming = await WriteReadyForArchiveAsync(capture, capture.CameraId);
        var archive = Path.Combine(Path.GetTempPath(), "garagewatch-retention-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archive);
        await WriteArchiveCopyAsync(archive, capture, old);
        await SavePolicyAsync(factory, capture.CameraId, maximumBytes: 10);
        var options = new ClipArchiveOptions(archive, 1, 100, long.MaxValue, long.MaxValue);
        var capacityManager = new ClipRetentionWorker(options, capture,
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<ClipReadLeaseRegistry>(), TimeProvider.System,
            NullLogger<ClipRetentionWorker>.Instance);
        var worker = new ClipArchiveWorker(capture, options, new LocalFilesystemClipArchiveAdapter(options),
            capacityManager, NullLogger<ClipArchiveWorker>.Instance);
        try
        {
            await worker.RunOnceAsync(CancellationToken.None);
            Assert.False(Directory.Exists(Path.Combine(archive, capture.CameraId, old.ClipId)));
            var source = await ReadClipAsync(capture, incoming.ClipId);
            Assert.True(Directory.Exists(Path.Combine(archive, capture.CameraId, incoming.ClipId)),
                $"New archive status was {source?.ArchiveStatus}.");
            Assert.True(File.Exists(Path.Combine(capture.SpoolDirectory, "clips", "media", old.MediaFileName!)));
            Assert.Equal("archived", source!.ArchiveStatus);
        }
        finally
        {
            Directory.Delete(archive, recursive: true);
        }
    }

    [Fact]
    public async Task Archive_worker_waits_when_only_preserved_copies_block_per_camera_quota()
    {
        await using var factory = new ApiFactory();
        var capture = factory.Services.GetRequiredService<CaptureOptions>();
        var protectedClip = await WriteArchivedClipAsync(capture, capture.CameraId, DateTimeOffset.UtcNow);
        var incoming = await WriteReadyForArchiveAsync(capture, capture.CameraId);
        var archive = Path.Combine(Path.GetTempPath(), "garagewatch-retention-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(archive);
        await WriteArchiveCopyAsync(archive, capture, protectedClip);
        await SavePolicyAsync(factory, capture.CameraId, maximumBytes: 10, preserveClipId: protectedClip.ClipId);
        var options = new ClipArchiveOptions(archive, 1, 100, long.MaxValue, long.MaxValue);
        var capacityManager = new ClipRetentionWorker(options, capture,
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<ClipReadLeaseRegistry>(), TimeProvider.System,
            NullLogger<ClipRetentionWorker>.Instance);
        var worker = new ClipArchiveWorker(capture, options, new LocalFilesystemClipArchiveAdapter(options),
            capacityManager, NullLogger<ClipArchiveWorker>.Instance);
        try
        {
            await worker.RunOnceAsync(CancellationToken.None);
            Assert.True(Directory.Exists(Path.Combine(archive, capture.CameraId, protectedClip.ClipId)));
            Assert.False(Directory.Exists(Path.Combine(archive, capture.CameraId, incoming.ClipId)));
            Assert.Equal("retry_wait", (await ReadClipAsync(capture, incoming.ClipId))!.ArchiveStatus);
        }
        finally
        {
            Directory.Delete(archive, recursive: true);
        }
    }

    private static async Task<ClipManifestV1> WriteArchivedClipAsync(CaptureOptions capture, string cameraId,
        DateTimeOffset archivedAt)
    {
        var id = Guid.NewGuid().ToString("N");
        var clipPublicId = new PublicIdGenerator().Generate(PublicIdPrefixes.ClipPrefix);
        var clipRoot = Path.Combine(capture.SpoolDirectory, "clips");
        var mediaDir = Path.Combine(clipRoot, "media");
        var thumbnailDir = Path.Combine(clipRoot, "thumbnails");
        var manifestDir = Path.Combine(clipRoot, "manifests");
        Directory.CreateDirectory(mediaDir);
        Directory.CreateDirectory(thumbnailDir);
        Directory.CreateDirectory(manifestDir);
        var mediaName = id + ".ts";
        var thumbnailName = id + ".jpg";
        var mediaPath = Path.Combine(mediaDir, mediaName);
        var thumbnailPath = Path.Combine(thumbnailDir, thumbnailName);
        await File.WriteAllBytesAsync(mediaPath, [0x47, 0x01, 0x02]);
        await File.WriteAllBytesAsync(thumbnailPath, [0xff, 0xd8, 0xff, 0xd9]);
        var start = archivedAt.AddMinutes(-1);
        var clip = new ClipManifestV1("1.0", id, cameraId, start, start.AddMinutes(1), start,
            start.AddMinutes(1), "ready", "trigger-v1", "visual_change:test", [Guid.NewGuid().ToString("N")],
            mediaName, await HashAsync(mediaPath), thumbnailName, await HashAsync(thumbnailPath), 1, archivedAt,
            ArchiveStatus: "archived", ArchivedAt: archivedAt, PublicId: clipPublicId);
        await File.WriteAllTextAsync(Path.Combine(manifestDir, id + ".json"),
            JsonSerializer.Serialize(clip, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return clip;
    }

    private static async Task<ClipManifestV1> WriteReadyForArchiveAsync(CaptureOptions capture, string cameraId)
    {
        var ready = (await WriteArchivedClipAsync(capture, cameraId, DateTimeOffset.UtcNow)) with
        {
            ArchiveStatus = "not_requested",
            ArchivedAt = null,
            ArchiveManifestSha256 = null
        };
        await File.WriteAllTextAsync(Path.Combine(capture.SpoolDirectory, "clips", "manifests", ready.ClipId + ".json"),
            JsonSerializer.Serialize(ready, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return ready;
    }

    private static async Task SavePolicyAsync(ApiFactory factory, string cameraId, long maximumBytes,
        string? preserveClipId = null)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var now = DateTimeOffset.UtcNow;
        database.ClipRetentionPolicies.Add(new ClipRetentionPolicy
        {
            CameraId = cameraId,
            RetentionDays = 3650,
            MaximumStorageBytes = maximumBytes,
            Version = 1,
            UpdatedByUserId = Guid.Empty.ToString("D"),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        if (preserveClipId is not null)
        {
            var clipPath = Path.Combine(factory.Services.GetRequiredService<CaptureOptions>().SpoolDirectory,
                "clips", "manifests", preserveClipId + ".json");
            var clip = JsonSerializer.Deserialize<ClipManifestV1>(await File.ReadAllTextAsync(clipPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            database.ClipPreservations.Add(new ClipPreservation
            {
                ClipId = clip.ClipId,
                PublicId = clip.PublicId!,
                CameraId = clip.CameraId,
                IsPreserved = true,
                Version = 1,
                UpdatedByUserId = Guid.Empty.ToString("D"),
                UpdatedAtUtc = now
            });
        }
        await database.SaveChangesAsync();
    }

    private static async Task<ClipManifestV1?> ReadClipAsync(CaptureOptions capture, string clipId) =>
        JsonSerializer.Deserialize<ClipManifestV1>(await File.ReadAllTextAsync(Path.Combine(capture.SpoolDirectory,
            "clips", "manifests", clipId + ".json")), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static async Task WriteArchiveCopyAsync(string archiveRoot, CaptureOptions capture, ClipManifestV1 clip)
    {
        var directory = Path.Combine(archiveRoot, clip.CameraId, clip.ClipId);
        Directory.CreateDirectory(directory);
        var mediaPath = Path.Combine(directory, clip.MediaFileName!);
        var thumbnailPath = Path.Combine(directory, clip.ThumbnailFileName!);
        // The exact synthetic source bytes are known; the archive contract is finalized by the manifest hashes.
        await File.WriteAllBytesAsync(mediaPath, [0x47, 0x01, 0x02]);
        await File.WriteAllBytesAsync(thumbnailPath, [0xff, 0xd8, 0xff, 0xd9]);
        var mediaSize = new FileInfo(mediaPath).Length;
        var thumbnailSize = new FileInfo(thumbnailPath).Length;
        var archived = new ArchivedClipManifestV1("1.0", clip.ClipId, clip.PublicId!, clip.CameraId,
            clip.RequestedStartedAt, clip.RequestedEndedAt, clip.EffectiveStartedAt, clip.EffectiveEndedAt,
            clip.TriggerConfigVersion, clip.TriggerReason, clip.SegmentIds, clip.MediaFileName!, mediaSize,
            clip.MediaSha256!, clip.ThumbnailFileName!, thumbnailSize, clip.ThumbnailSha256!, clip.ArchivedAt!.Value);
        var commitPath = Path.Combine(directory, "archive-manifest.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(archived, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllBytesAsync(commitPath, bytes);
        var commitHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var sourcePath = Path.Combine(capture.SpoolDirectory, "clips", "manifests", clip.ClipId + ".json");
        await File.WriteAllTextAsync(sourcePath, JsonSerializer.Serialize(clip with
        {
            ArchiveManifestSha256 = commitHash
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static async Task<string> GetCameraPublicIdAsync(ApiFactory factory, string internalCameraId)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IamDbContext>().CameraCatalog.AsNoTracking()
            .Where(item => item.CameraId == internalCameraId).Select(item => item.PublicId).SingleAsync())!;
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static async Task<HttpClient> LoginAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        return client;
    }
}
