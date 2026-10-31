using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using GarageWatch.Application.Analysis;
using GarageWatch.Application.Events;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Analysis;

public sealed class ObjectDetectionWorker : BackgroundService
{
    private sealed record WorkItem(SegmentManifestV1 Manifest, string SegmentPath);
    private static readonly Meter Meter = new("GarageWatch.Analysis", "1.0.0");
    private static readonly Counter<long> Processed = Meter.CreateCounter<long>("garagewatch.analysis.frames");
    private static readonly Counter<long> Dropped = Meter.CreateCounter<long>("garagewatch.analysis.dropped");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("garagewatch.analysis.duration", unit: "s");
    private readonly Channel<WorkItem> _queue;
    private readonly CaptureOptions _capture;
    private readonly OnnxDetectorOptions _options;
    private readonly DetectionProcessor _processor;
    private readonly ILogger<ObjectDetectionWorker> _logger;
    private readonly EventEngineOptions _eventOptions;
    private readonly VersionedEventRuleEngine _eventEngine;
    private readonly LocalEventJournal _eventJournal;
    private int _disabled;
    private long _segmentsSeen;

    public ObjectDetectionWorker(CaptureOptions capture, OnnxDetectorOptions options, DetectionProcessor processor,
        ILogger<ObjectDetectionWorker> logger, EventEngineOptions? eventOptions = null,
        VersionedEventRuleEngine? eventEngine = null, LocalEventJournal? eventJournal = null)
    {
        _capture = capture;
        _options = options;
        _processor = processor;
        _logger = logger;
        _eventOptions = eventOptions ?? new EventEngineOptions(null, null, Path.Combine(capture.SpoolDirectory, "events"),
            10_000, 67_108_864);
        _eventEngine = eventEngine ?? new VersionedEventRuleEngine();
        _eventJournal = eventJournal ?? new LocalEventJournal(_eventOptions);
        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public bool TryEnqueue(SegmentManifestV1 manifest, string segmentPath)
    {
        if (!_options.Enabled || Volatile.Read(ref _disabled) != 0) return false;
        if (Interlocked.Increment(ref _segmentsSeen) % _options.SampleEverySegments != 0) return false;
        if (_queue.Writer.TryWrite(new WorkItem(manifest, segmentPath))) return true;
        Dropped.Add(1, new KeyValuePair<string, object?>("reason", "queue_full"));
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_eventOptions.ConfigurationError is not null)
            _logger.LogWarning("Regras de evento inválidas; eventos desativados sem afetar captura/análise, código={ErrorCode}",
                _eventOptions.ConfigurationError);
        else if (_eventOptions.Rules is not null)
        {
            try
            {
                var recovery = await _eventJournal.RecoverAsync(stoppingToken);
                if (recovery.InvalidEvents > 0)
                    _logger.LogWarning("Journal de eventos contém registros inválidos: {InvalidCount}", recovery.InvalidEvents);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Journal de eventos indisponível; captura e análise seguem independentes, motivo={FailureType}",
                    exception.GetType().Name);
            }
        }
        if (_options.ConfigurationError is not null)
        {
            Volatile.Write(ref _disabled, 1);
            _logger.LogWarning("Detector não configurado; análise desativada sem afetar captura, código={ErrorCode}",
                _options.ConfigurationError);
            return;
        }
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var frame = await DecodeAsync(item, stoppingToken);
                var started = Stopwatch.GetTimestamp();
                var result = await _processor.ProcessAsync(frame, stoppingToken);
                await EvaluateEventsAsync(result, stoppingToken);
                Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
                Processed.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
                _logger.LogDebug("Amostra de análise concluída: {DetectionCount} detecções", result.Detections.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                Processed.Add(1, new KeyValuePair<string, object?>("outcome", "failure"));
                var fatal = exception is InvalidOperationException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException;
                var firstFailure = fatal && Interlocked.Exchange(ref _disabled, 1) == 0;
                if (firstFailure)
                    _logger.LogWarning("Adaptador de análise desativado após falha; captura permanece independente, motivo={FailureType}",
                        exception.GetType().Name);
                else
                    _logger.LogDebug("Amostra de análise descartada; motivo={FailureType}", exception.GetType().Name);
            }
        }
    }

    private async Task EvaluateEventsAsync(DetectionResultV1 result, CancellationToken cancellationToken)
    {
        if (_eventOptions.Rules is null || _eventOptions.ConfigurationError is not null) return;
        try
        {
            var generated = _eventEngine.Process(result, _eventOptions.Rules);
            foreach (var occurrence in generated.Events)
            {
                var appendStatus = await _eventJournal.AppendAsync(occurrence, cancellationToken);
                if (appendStatus == EventAppendStatus.CapacityReached)
                    _logger.LogWarning("Cota do journal de eventos atingida; ocorrência descartada, regra={RuleId}", occurrence.RuleId);
            }
            if (generated.DroppedStateUpdates > 0)
                _logger.LogWarning("Limite de estado temporal de regras atingido; observações descartadas={DropCount}",
                    generated.DroppedStateUpdates);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            _logger.LogWarning("Regras de evento falharam; captura/análise seguem independentes, motivo={FailureType}",
                exception.GetType().Name);
        }
    }

    private async Task<DetectionFrame> DecodeAsync(WorkItem item, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_capture.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", item.SegmentPath,
                     "-frames:v", "1", "-vf", $"scale={_options.Width}:{_options.Height}", "-pix_fmt", "rgb24",
                     "-f", "rawvideo", "pipe:1" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidDataException("analysis_frame_decode_failed");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        var frameBytes = new byte[checked(_options.Width * _options.Height * 3)];
        var readTask = process.StandardOutput.BaseStream.ReadExactlyAsync(frameBytes, timeout.Token).AsTask();
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        await Task.WhenAll(readTask, errorTask);
        if (process.ExitCode != 0) throw new InvalidDataException("analysis_frame_decode_failed");
        var plane = _options.Width * _options.Height;
        var values = new float[frameBytes.Length];
        for (var pixel = 0; pixel < plane; pixel++)
        {
            values[pixel] = frameBytes[pixel * 3] / 255f;
            values[plane + pixel] = frameBytes[pixel * 3 + 1] / 255f;
            values[2 * plane + pixel] = frameBytes[pixel * 3 + 2] / 255f;
        }
        return new DetectionFrame(item.Manifest.CameraId, item.Manifest.StreamEpoch, item.Manifest.SegmentId,
            item.Manifest.StartedAt, _options.Width, _options.Height, values);
    }
}
