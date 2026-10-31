using System.Diagnostics.Metrics;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using GarageWatch.Infrastructure.Analysis;

namespace GarageWatch.Infrastructure.Capture;

public sealed class CaptureWorker(
    CaptureOptions options,
    ISegmentCaptureAdapter adapter,
    LocalSegmentSpool spool,
    ILogger<CaptureWorker> logger,
    ClipTriggerWorker? clipTrigger = null,
    ObjectDetectionWorker? detectionWorker = null) : BackgroundService
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);
    private static readonly Meter Meter = new("GarageWatch.Capture", "1.0.0");
    private static readonly Counter<long> Segments = Meter.CreateCounter<long>("garagewatch.capture.segments");
    private static readonly Counter<long> SegmentBytes = Meter.CreateCounter<long>("garagewatch.capture.segment.bytes", unit: "By");
    private static readonly Histogram<double> SegmentDuration = Meter.CreateHistogram<double>("garagewatch.capture.segment.duration", unit: "s");
    private static readonly Counter<long> Gaps = Meter.CreateCounter<long>("garagewatch.capture.gaps");
    private static readonly Counter<long> Reconnects = Meter.CreateCounter<long>("garagewatch.capture.reconnects");
    private readonly object _gate = new();
    private CaptureStatus _status = new(adapter.Name, adapter.Version, adapter.Capabilities, "starting", null, 0, 0, null);

    public CaptureStatus Status { get { lock (_gate) return _status; } }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var recoveredSegments = await spool.RecoverAsync(stoppingToken);
                lock (_gate) _status = _status with { Gaps = spool.CountGaps(), Segments = recoveredSegments };
                UpdateRetainedSegments(EnforceRetention());
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                Update("spool-unavailable", null, "local_spool_unavailable");
                logger.LogWarning("Spool local indisponível; recuperação será repetida, motivo={FailureType}", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        var failures = 0;
        string? lastSuccessfulEpoch = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var epoch = Guid.NewGuid().ToString();
            var workDirectory = Path.Combine(options.SpoolDirectory, "work", epoch);
            if (failures == 0) Update("connecting", null, null);
            try
            {
                await ConsumeAttemptAsync(workDirectory, stoppingToken, OnSegmentAsync);
                if (!stoppingToken.IsCancellationRequested) throw new InvalidOperationException("capture_stream_ended");

                async Task OnSegmentAsync(CapturedSegment segment)
                {
                    var manifest = await spool.StoreAsync(segment, epoch, stoppingToken);
                    File.Delete(segment.SourcePath);
                    failures = 0;
                    lastSuccessfulEpoch = epoch;
                    Update("capturing", epoch, null, segmentsDelta: 1);
                    var metricTags = CreateMetricTags("success");
                    Segments.Add(1, metricTags);
                    SegmentBytes.Add(manifest.SizeBytes, metricTags);
                    SegmentDuration.Record(Math.Max(0, (segment.EndedAt - segment.StartedAt).TotalSeconds), metricTags);
                    logger.LogInformation("Segmento de captura confirmado: {SegmentId}, {SizeBytes} bytes", manifest.SegmentId, manifest.SizeBytes);
                    UpdateRetainedSegments(EnforceRetention());
                    clipTrigger?.TryEnqueue(manifest);
                    detectionWorker?.TryEnqueue(manifest, Path.Combine(options.SpoolDirectory, "segments", manifest.FileName));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                failures = Math.Min(failures + 1, 6);
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await spool.RecordGapAsync(lastSuccessfulEpoch, failures, stoppingToken);
                        break;
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception spoolException)
                    {
                        Update("spool-unavailable", null, "local_spool_unavailable");
                        logger.LogWarning("Gap não confirmado no spool; captura aguardará recuperação, motivo={FailureType}",
                            spoolException.GetType().Name);
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    }
                }
                if (stoppingToken.IsCancellationRequested) break;
                var errorCode = exception is TimeoutException { Message: "capture_segment_timeout" }
                    ? "capture_segment_timeout"
                    : exception is InvalidDataException { Message: "capture_codec_unsupported" }
                    ? "capture_codec_unsupported"
                    : exception is InvalidDataException { Message: "capture_audio_unsupported" }
                        ? "capture_audio_unsupported"
                    : exception is InvalidDataException { Message: "capture_segment_size_limit" }
                        ? "capture_segment_size_limit"
                        : "capture_source_unavailable";
                Update("reconnecting", null, errorCode);
                UpdateRetainedGaps(spool.CountGaps());
                Gaps.Add(1, CreateMetricTags("gap"));
                Reconnects.Add(1, CreateMetricTags("retry"));
                logger.LogWarning("Fonte de captura indisponível; nova tentativa limitada, motivo={FailureType}", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << (failures - 1))), stoppingToken);
            }
            finally { TryDeleteDirectory(workDirectory); }
        }
        Update("stopped", null, null);
    }

    private async Task ConsumeAttemptAsync(string workDirectory, CancellationToken stoppingToken,
        Func<CapturedSegment, Task> onSegment)
    {
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await using var enumerator = adapter.CaptureAsync(workDirectory, attemptCancellation.Token)
            .GetAsyncEnumerator(attemptCancellation.Token);
        while (true)
        {
            var moveNext = enumerator.MoveNextAsync().AsTask();
            bool hasNext;
            try
            {
                hasNext = await moveNext.WaitAsync(TimeSpan.FromSeconds(options.EffectiveSegmentTimeoutSeconds), stoppingToken);
            }
            catch (TimeoutException)
            {
                attemptCancellation.Cancel();
                try { await moveNext; }
                catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested) { }
                throw new TimeoutException("capture_segment_timeout");
            }

            if (!hasNext) return;
            await onSegment(enumerator.Current);
        }
    }

    private int EnforceRetention()
    {
        var manifests = Directory.GetFiles(Path.Combine(options.SpoolDirectory, "manifests"), "*.json")
            .OrderBy(File.GetCreationTimeUtc).ToArray();
        var pinned = spool.GetPinnedSegmentIds();
        var retained = manifests.Length;
        foreach (var old in manifests.Where(path => !pinned.Contains(Path.GetFileNameWithoutExtension(path)))
                     .Take(Math.Max(0, manifests.Length - options.MaximumSegments)))
        {
            var segmentId = Path.GetFileNameWithoutExtension(old);
            try
            {
                var manifest = System.Text.Json.JsonSerializer.Deserialize<SegmentManifestV1>(File.ReadAllText(old), JsonOptions);
                if (LocalSegmentSpool.IsValidManifest(manifest, segmentId, options.CameraId, options.MaximumSegmentBytes))
                    File.Delete(Path.Combine(options.SpoolDirectory, "segments", segmentId + ".ts"));
                else if (LocalSegmentSpool.IsSafeSegmentId(segmentId))
                    File.Delete(Path.Combine(options.SpoolDirectory, "segments", segmentId + ".ts"));
                File.Delete(old);
                if (!File.Exists(old)) retained--;
            }
            catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
            {
                logger.LogWarning("Manifesto antigo não pôde ser reconciliado; motivo={FailureType}", exception.GetType().Name);
            }
        }
        return retained;
    }

    private void UpdateRetainedSegments(int count)
    {
        lock (_gate) _status = _status with { Segments = count };
    }

    private void UpdateRetainedGaps(long count)
    {
        lock (_gate) _status = _status with { Gaps = count };
    }

    private void Update(string state, string? epoch, string? error, long gapsDelta = 0, long segmentsDelta = 0)
    {
        lock (_gate) _status = _status with
        {
            State = state,
            StreamEpoch = epoch,
            LastErrorCode = error,
            Gaps = _status.Gaps + gapsDelta,
            Segments = _status.Segments + segmentsDelta
        };
    }

    private TagList CreateMetricTags(string outcome) => new()
    {
        { "adapter", adapter.Name },
        { "adapter_version", adapter.Version },
        { "capability_profile", string.Join(',', adapter.Capabilities) },
        { "outcome", outcome }
    };

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { }
    }
}
