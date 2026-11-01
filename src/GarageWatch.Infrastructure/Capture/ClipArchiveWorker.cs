using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Capture;

public sealed class ClipArchiveWorker(CaptureOptions capture, ClipArchiveOptions options,
    IClipArchiveAdapter adapter, IClipArchiveCapacityManager capacityManager,
    ILogger<ClipArchiveWorker> logger) : BackgroundService
{
    private const int MaximumAttempts = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Meter Meter = new("GarageWatch.ClipArchive", "1.0.0");
    private static readonly Counter<long> Attempts = Meter.CreateCounter<long>("garagewatch.clip_archive.attempts");
    private static readonly Counter<long> Transitions = Meter.CreateCounter<long>("garagewatch.clip_archive.transitions");
    private static readonly Counter<long> Dropped = Meter.CreateCounter<long>("garagewatch.clip_archive.dropped");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        var manifestDirectory = Path.Combine(capture.SpoolDirectory, "clips", "manifests");
        Directory.CreateDirectory(manifestDirectory);

        await RecoverInterruptedAsync(manifestDirectory, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollSeconds));
        do
        {
            try { await ProcessReadyClipsAsync(manifestDirectory, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Varredura de arquivamento falhou; motivo={FailureType}", exception.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var manifestDirectory = Path.Combine(capture.SpoolDirectory, "clips", "manifests");
        Directory.CreateDirectory(manifestDirectory);
        return ProcessReadyClipsAsync(manifestDirectory, cancellationToken);
    }

    private async Task RecoverInterruptedAsync(string manifestDirectory, CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(manifestDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clip = await ReadClipAsync(path, cancellationToken);
            if (clip is null || clip.State != "ready" || clip.ArchiveStatus is not ("copying" or "verifying")) continue;
            await PersistAsync(path, clip with
            {
                ArchiveStatus = "retry_wait",
                ArchiveNextAttemptAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);
            Transitions.Add(1, new KeyValuePair<string, object?>("state", "retry_wait"));
        }
    }

    private async Task ProcessReadyClipsAsync(string manifestDirectory, CancellationToken cancellationToken)
    {
        foreach (var path in Directory.EnumerateFiles(manifestDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clip = await ReadClipAsync(path, cancellationToken);
            if (clip is null || clip.SchemaVersion != "1.0" || clip.State != "ready"
                || !LocalSegmentSpool.IsSafeSegmentId(clip.ClipId)
                || Path.GetFileNameWithoutExtension(path) != clip.ClipId) continue;
            if (clip.ArchiveStatus is "archived" or "expiring" or "expired" or "failed") continue;
            if (clip.ArchiveNextAttemptAt is { } next && next > DateTimeOffset.UtcNow) continue;

            if (clip.ArchiveStatus is not ("pending" or "retry_wait" or "copying" or "verifying"))
            {
                clip = clip with { ArchiveStatus = "pending", ArchiveNextAttemptAt = DateTimeOffset.UtcNow };
                await PersistAsync(path, clip, cancellationToken);
                Transitions.Add(1, new KeyValuePair<string, object?>("state", "pending"));
            }
            await ArchiveOneAsync(path, clip, cancellationToken);
        }
    }

    private async Task ArchiveOneAsync(string manifestPath, ClipManifestV1 clip, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var mediaPath = Path.Combine(capture.SpoolDirectory, "clips", "media", clip.MediaFileName ?? string.Empty);
        var thumbnailPath = Path.Combine(capture.SpoolDirectory, "clips", "thumbnails", clip.ThumbnailFileName ?? string.Empty);
        try
        {
            var incomingBytes = File.Exists(mediaPath) && File.Exists(thumbnailPath)
                ? checked(new FileInfo(mediaPath).Length + new FileInfo(thumbnailPath).Length)
                : 0;
            if (!await capacityManager.EnsureCapacityAsync(clip.CameraId, incomingBytes, cancellationToken))
            {
                await PersistAsync(manifestPath, clip with
                {
                    ArchiveStatus = "retry_wait",
                    ArchiveNextAttemptAt = startedAt.AddSeconds(options.PollSeconds),
                    UpdatedAt = startedAt
                }, cancellationToken);
                logger.LogWarning("Arquivamento aguardando capacidade por câmera; clip={ClipId}", clip.ClipId);
                return;
            }
            await PersistAsync(manifestPath, clip with
            {
                ArchiveStatus = "copying",
                ArchiveAttemptCount = clip.ArchiveAttemptCount + 1,
                ArchiveNextAttemptAt = null,
                UpdatedAt = startedAt
            }, cancellationToken);
            Attempts.Add(1, new KeyValuePair<string, object?>("outcome", "started"));
            var receipt = await adapter.ArchiveAsync(clip, mediaPath, thumbnailPath, cancellationToken);
            var verifying = clip with
            {
                ArchiveStatus = "verifying",
                ArchiveAttemptCount = clip.ArchiveAttemptCount + 1,
                ArchiveNextAttemptAt = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await PersistAsync(manifestPath, verifying, cancellationToken);
            var archived = verifying with
            {
                ArchiveStatus = "archived",
                ArchivedAt = receipt.ArchivedAt,
                ArchiveManifestSha256 = receipt.ManifestSha256,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await PersistAsync(manifestPath, archived, cancellationToken);
            Transitions.Add(1, new KeyValuePair<string, object?>("state", "archived"));
            Attempts.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
            logger.LogInformation("Clipe arquivado e verificado: {ClipId}", clip.ClipId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (InvalidDataException exception)
        {
            await PersistAsync(manifestPath, clip with
            {
                ArchiveStatus = "failed",
                ArchiveAttemptCount = clip.ArchiveAttemptCount + 1,
                ArchiveNextAttemptAt = null,
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);
            Transitions.Add(1, new KeyValuePair<string, object?>("state", "failed"));
            Dropped.Add(1, new KeyValuePair<string, object?>("reason", "invalid_source"));
            logger.LogWarning("Clipe rejeitado pelo contrato de arquivo; clip={ClipId}, motivo={FailureType}",
                clip.ClipId, exception.GetType().Name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var attempt = clip.ArchiveAttemptCount + 1;
            var terminal = attempt >= MaximumAttempts;
            var delaySeconds = Math.Min(300, Math.Pow(2, Math.Min(attempt, 8)));
            var retry = clip with
            {
                ArchiveStatus = terminal ? "failed" : "retry_wait",
                ArchiveAttemptCount = attempt,
                ArchiveNextAttemptAt = terminal ? null : DateTimeOffset.UtcNow.AddSeconds(delaySeconds),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await PersistAsync(manifestPath, retry, cancellationToken);
            Transitions.Add(1, new KeyValuePair<string, object?>("state", retry.ArchiveStatus));
            Attempts.Add(1, new KeyValuePair<string, object?>("outcome", terminal ? "exhausted" : "retry"));
            logger.LogWarning("Arquivamento local falhou; clip={ClipId}, attempt={Attempt}, motivo={FailureType}",
                clip.ClipId, attempt, exception.GetType().Name);
        }
    }

    private static async Task<ClipManifestV1?> ReadClipAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                16_384, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<ClipManifestV1>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or JsonException) { return null; }
    }

    private static async Task PersistAsync(string path, ClipManifestV1 clip, CancellationToken cancellationToken)
    {
        var temporary = path + ".archive.tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                         16_384, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, clip, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
