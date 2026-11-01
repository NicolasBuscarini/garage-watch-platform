using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Capture;

public interface IClipArchiveCapacityManager
{
    Task<bool> EnsureCapacityAsync(string cameraId, long incomingBytes, CancellationToken cancellationToken);
}

/// <summary>Expires only committed copies in the archive tree; spool sources and manifests are never deleted.</summary>
public sealed class ClipRetentionWorker(ClipArchiveOptions options, CaptureOptions capture, IServiceScopeFactory scopes,
    ClipReadLeaseRegistry leases, TimeProvider timeProvider, ILogger<ClipRetentionWorker> logger)
    : BackgroundService, IClipArchiveCapacityManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Meter Meter = new("GarageWatch.ClipRetention", "1.0.0");
    private static readonly Counter<long> Expired = Meter.CreateCounter<long>("garagewatch.clip_retention.expired");
    private static readonly Counter<long> Protected = Meter.CreateCounter<long>("garagewatch.clip_retention.protected");
    private readonly SemaphoreSlim runGate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds));
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Varredura de retenção de clipes falhou; motivo={FailureType}",
                    exception.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await runGate.WaitAsync(cancellationToken);
        try { await RunOnceCoreAsync(null, 0, cancellationToken); }
        finally { runGate.Release(); }
    }

    public async Task<bool> EnsureCapacityAsync(string cameraId, long incomingBytes,
        CancellationToken cancellationToken)
    {
        if (incomingBytes < 0) throw new ArgumentOutOfRangeException(nameof(incomingBytes));
        await using var scope = scopes.CreateAsyncScope();
        var retention = scope.ServiceProvider.GetRequiredService<ClipRetentionService>();
        var policy = await retention.GetWorkerSnapshotAsync(cameraId, cancellationToken);
        if (policy.MaximumStorageBytes is not { } limit) return true;
        if (incomingBytes > limit) return false;

        await runGate.WaitAsync(cancellationToken);
        try { await RunOnceCoreAsync(cameraId, incomingBytes, cancellationToken); }
        finally { runGate.Release(); }

        var cameraDirectory = Path.Combine(options.DestinationDirectory!, cameraId);
        var currentBytes = MeasureCameraBytes(cameraDirectory);
        return currentBytes <= limit && incomingBytes <= limit - currentBytes;
    }

    private async Task RunOnceCoreAsync(string? reservedCameraId, long reservedBytes,
        CancellationToken cancellationToken)
    {
        var archiveRoot = options.DestinationDirectory!;
        if (!Directory.Exists(archiveRoot)) return;
        if ((File.GetAttributes(archiveRoot) & FileAttributes.ReparsePoint) != 0) return;
        await ReconcileExpiringAsync(archiveRoot, cancellationToken);
        var entries = new List<ClipRetentionEntry>();
        foreach (var cameraDirectory in Directory.EnumerateDirectories(archiveRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(cameraDirectory) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var clipDirectory in Directory.EnumerateDirectories(cameraDirectory))
                if (ClipRetentionService.TryReadArchive(clipDirectory, out var entry) && entry is not null &&
                    Path.GetFileName(cameraDirectory) == entry.CameraId &&
                    await IsSourceCommitConfirmedAsync(entry, cancellationToken))
                    entries.Add(entry);
        }

        foreach (var group in entries.GroupBy(entry => entry.CameraId, StringComparer.Ordinal))
        {
            await using var scope = scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ClipRetentionService>();
            var policy = await service.GetWorkerSnapshotAsync(group.Key, cancellationToken);
            var ordered = group.OrderBy(entry => entry.StartedAt).ThenBy(entry => entry.ClipId,
                StringComparer.Ordinal).ToArray();
            var totalBytes = ordered.Sum(entry => entry.SizeBytes);
            var expiredBefore = timeProvider.GetUtcNow().AddDays(-policy.RetentionDays);
            foreach (var entry in ordered)
            {
                var expiresByAge = entry.EndedAt <= expiredBefore;
                var reserved = group.Key == reservedCameraId ? reservedBytes : 0;
                var overLimit = policy.MaximumStorageBytes is { } limit &&
                    (reserved > limit || totalBytes > limit - reserved);
                if (!expiresByAge && !overLimit) continue;
                if (policy.PreservedClipIds.Contains(entry.ClipId) || policy.ActiveClipIds.Contains(entry.ClipId) ||
                    await service.IsClipProtectedAsync(entry.ClipId, cancellationToken))
                {
                    Protected.Add(1);
                    continue;
                }

                using var purge = leases.TryBeginPurge(entry.PublicId);
                if (purge is null || await service.IsClipProtectedAsync(entry.ClipId, cancellationToken))
                {
                    Protected.Add(1);
                    continue;
                }
                if (!await VerifyArchiveAsync(entry, cancellationToken))
                {
                    logger.LogWarning("Expurgo local de clipe ignorado por falha de integridade ou caminho inseguro; clip={ClipId}",
                        entry.ClipId);
                    continue;
                }
                await MarkArchiveStatusAsync(entry, "expiring", cancellationToken);
                if (!TryDeleteCommittedCopy(entry.Directory))
                {
                    logger.LogWarning("Expurgo tombstonado aguarda reconciliação por falha no filesystem; clip={ClipId}",
                        entry.ClipId);
                    continue;
                }
                totalBytes -= entry.SizeBytes;
                await MarkArchiveStatusAsync(entry, "expired", cancellationToken);
                await service.RecordExpiredAsync(entry.PublicId, cancellationToken);
                Expired.Add(1);
            }
        }
    }

    private static long MeasureCameraBytes(string cameraDirectory)
    {
        if (!Directory.Exists(cameraDirectory)) return 0;
        if ((File.GetAttributes(cameraDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("clip_archive_camera_path_unsafe");
        long total = 0;
        foreach (var directory in Directory.EnumerateDirectories(cameraDirectory))
            if (ClipRetentionService.TryReadArchive(directory, out var entry) && entry is not null)
                total = checked(total + entry.SizeBytes);
        return total;
    }

    private async Task ReconcileExpiringAsync(string archiveRoot, CancellationToken cancellationToken)
    {
        var manifestDirectory = Path.Combine(capture.SpoolDirectory, "clips", "manifests");
        if (!Directory.Exists(manifestDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(manifestDirectory, "*.json").Take(10_000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clip = await ReadSourceManifestAsync(path, cancellationToken);
            if (clip is not { State: "ready", ArchiveStatus: "expiring" } ||
                !LocalSegmentSpool.IsSafeSegmentId(clip.ClipId) ||
                !PublicIdFormat.IsValid(clip.PublicId, PublicIdPrefixes.ClipPrefix)) continue;
            using var purge = leases.TryBeginPurge(clip.PublicId!);
            if (purge is null) continue;
            await using var scope = scopes.CreateAsyncScope();
            var retention = scope.ServiceProvider.GetRequiredService<ClipRetentionService>();
            if (await retention.IsClipProtectedAsync(clip.ClipId, cancellationToken))
            {
                await PersistSourceManifestAsync(path, clip with { ArchiveStatus = "archived" }, cancellationToken);
                continue;
            }
            var directory = Path.Combine(archiveRoot, clip.CameraId, clip.ClipId);
            if (Directory.Exists(directory))
            {
                if (!ClipRetentionService.TryReadArchive(directory, out var entry) || entry is null ||
                    !await VerifyArchiveAsync(entry, cancellationToken) || !TryDeleteCommittedCopy(directory)) continue;
            }
            await PersistSourceManifestAsync(path, clip with { ArchiveStatus = "expired" }, cancellationToken);
            await retention.RecordExpiredAsync(clip.PublicId!, cancellationToken);
        }
    }

    private async Task MarkArchiveStatusAsync(ClipRetentionEntry entry, string status,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(capture.SpoolDirectory, "clips", "manifests", entry.ClipId + ".json");
        var clip = await ReadSourceManifestAsync(path, cancellationToken);
        var expectedState = status == "expiring" ? "archived" : "expiring";
        if (clip is not null && clip.ArchiveStatus == expectedState && clip.PublicId == entry.PublicId)
            await PersistSourceManifestAsync(path, clip with { ArchiveStatus = status }, cancellationToken);
    }

    private static async Task<ClipManifestV1?> ReadSourceManifestAsync(string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ClipManifestV1>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task PersistSourceManifestAsync(string path, ClipManifestV1 clip,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".retention.tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                         16_384, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, clip, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private async Task<bool> IsSourceCommitConfirmedAsync(ClipRetentionEntry entry, CancellationToken cancellationToken)
    {
        var sourceManifest = Path.Combine(capture.SpoolDirectory, "clips", "manifests", entry.ClipId + ".json");
        try
        {
            await using var stream = File.OpenRead(sourceManifest);
            var clip = await JsonSerializer.DeserializeAsync<ClipManifestV1>(stream, JsonOptions, cancellationToken);
            if (clip is not { SchemaVersion: "1.0", State: "ready", ArchiveStatus: "archived" } ||
                clip.ClipId != entry.ClipId || clip.PublicId != entry.PublicId || clip.CameraId != entry.CameraId ||
                string.IsNullOrWhiteSpace(clip.ArchiveManifestSha256)) return false;
            var archiveManifest = Path.Combine(entry.Directory, "archive-manifest.json");
            await using var archiveStream = File.OpenRead(archiveManifest);
            var hash = await SHA256.HashDataAsync(archiveStream, cancellationToken);
            return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(clip.ArchiveManifestSha256));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or FormatException)
        {
            return false;
        }
    }

    private static async Task<bool> VerifyArchiveAsync(ClipRetentionEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(entry.Directory, "archive-manifest.json");
            var manifest = JsonSerializer.Deserialize<ArchivedClipManifestV1>(await File.ReadAllTextAsync(path,
                cancellationToken), JsonOptions);
            if (manifest is null || manifest.ClipId != entry.ClipId || manifest.PublicId != entry.PublicId) return false;
            return await HasHashAsync(Path.Combine(entry.Directory, manifest.MediaFileName), manifest.MediaSha256,
                       cancellationToken) &&
                   await HasHashAsync(Path.Combine(entry.Directory, manifest.ThumbnailFileName),
                       manifest.ThumbnailSha256, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> HasHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken);
        return expected.Length == 64 && expected.All(Uri.IsHexDigit) &&
               CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected));
    }

    private static bool TryDeleteCommittedCopy(string directory)
    {
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
            var files = Directory.EnumerateFiles(directory).ToArray();
            if (files.Length != 3 || Directory.EnumerateDirectories(directory).Any() ||
                files.Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                return false;
            foreach (var file in files) File.Delete(file);
            Directory.Delete(directory, recursive: false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
