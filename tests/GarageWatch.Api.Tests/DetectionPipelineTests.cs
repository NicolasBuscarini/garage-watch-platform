using GarageWatch.Application.Analysis;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Analysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class DetectionPipelineTests
{
    [Fact]
    public void Detector_resource_controls_are_configurable_and_sampling_keeps_capture_enqueue_nonblocking()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DETECTOR_INTRA_OP_THREADS"] = "2",
            ["DETECTOR_INTER_OP_THREADS"] = "1",
            ["DETECTOR_SAMPLE_EVERY_SEGMENTS"] = "3",
            ["DETECTOR_QUEUE_CAPACITY"] = "1"
        }).Build();
        var configured = OnnxDetectorOptions.FromConfiguration(config);
        Assert.Equal(2, configured.IntraOpThreads);
        Assert.Equal(1, configured.InterOpThreads);
        Assert.Equal(3, configured.SampleEverySegments);

        var options = configured with { ModelPath = "/test/model.onnx" };
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var worker = new ObjectDetectionWorker(new CaptureOptions("synthetic", "camera-1", root, 1, 10,
            1_048_576, null, "ffmpeg", "ffprobe"), options,
            new DetectionProcessor(new ContractDetector(), new GreedyIouObjectTracker()),
            NullLogger<ObjectDetectionWorker>.Instance);
        var manifest = new SegmentManifestV1("1.0", "segment", "camera-1", "epoch-a", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, "segment.ts", 0, "hash");

        Assert.False(worker.TryEnqueue(manifest, "segment.ts"));
        Assert.False(worker.TryEnqueue(manifest, "segment.ts"));
        Assert.True(worker.TryEnqueue(manifest, "segment.ts"));
    }

    [Fact]
    public async Task Processor_validates_detector_output_and_assigns_stable_tracks()
    {
        var detector = new ContractDetector();
        var tracker = new GreedyIouObjectTracker();
        var processor = new DetectionProcessor(detector, tracker);
        var first = await processor.ProcessAsync(Frame("epoch-a", "frame-1"), CancellationToken.None);
        var second = await processor.ProcessAsync(Frame("epoch-a", "frame-2"), CancellationToken.None);

        Assert.Equal("1.0", first.SchemaVersion);
        var firstDetection = Assert.Single(first.Detections);
        var secondDetection = Assert.Single(second.Detections);
        Assert.Equal("camera-1:epoch-a:1", firstDetection.TrackId);
        Assert.Equal(firstDetection.TrackId, secondDetection.TrackId);
        Assert.Equal("person", secondDetection.ClassName);
    }

    [Fact]
    public async Task Tracker_never_carries_ids_across_epoch_or_camera()
    {
        var tracker = new GreedyIouObjectTracker();
        var detection = new ObjectDetection("person", 0, .9f, new DetectionBox(.1f, .1f, .4f, .8f));
        var first = tracker.Update(Frame("epoch-a", "f1"), [detection]).Single();
        var newEpoch = tracker.Update(Frame("epoch-b", "f2"), [detection]).Single();
        var otherCamera = tracker.Update(Frame("epoch-a", "f3", "camera-2"), [detection]).Single();

        Assert.NotEqual(first.TrackId, newEpoch.TrackId);
        Assert.NotEqual(first.TrackId, otherCamera.TrackId);
        Assert.StartsWith("camera-1:epoch-b:", newEpoch.TrackId);
    }

    [Fact]
    public async Task Processor_rejects_malformed_input_before_detector_execution()
    {
        var detector = new ContractDetector();
        var processor = new DetectionProcessor(detector, new GreedyIouObjectTracker());

        await Assert.ThrowsAsync<ArgumentException>(() => processor.ProcessAsync(
            Frame("epoch-a", "bad") with { RgbNchw = new float[1] }, CancellationToken.None).AsTask());
        Assert.Equal(0, detector.Calls);
    }

    [Fact]
    public void Iou_tracker_expires_unmatched_tracks_after_configured_window()
    {
        var tracker = new GreedyIouObjectTracker(maximumMissedFrames: 1);
        var detection = new ObjectDetection("person", 0, .9f, new DetectionBox(.1f, .1f, .4f, .8f));
        var first = tracker.Update(Frame("epoch-a", "f1"), [detection]).Single();
        Assert.Empty(tracker.Update(Frame("epoch-a", "f2"), []));
        Assert.Empty(tracker.Update(Frame("epoch-a", "f3"), []));
        var afterExpiration = tracker.Update(Frame("epoch-a", "f4"), [detection]).Single();

        Assert.NotEqual(first.TrackId, afterExpiration.TrackId);
    }

    [Fact]
    public async Task Onnx_adapter_defers_missing_model_failure_until_analysis_not_host_construction()
    {
        var options = new OnnxDetectorOptions("/missing/test-model.onnx", "test-v1", "images", "detections",
            2, 2, .25f, ["person"], 1, 1, 1, 1);
        using var detector = new OnnxObjectDetector(options);
        Assert.Equal("test-v1", detector.ModelVersion);

        await Assert.ThrowsAsync<InvalidOperationException>(() => detector.DetectAsync(
            Frame("epoch-a", "frame-1"), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Onnx_adapter_executes_deterministic_contract_graph()
    {
        var modelPath = Path.Combine(Path.GetTempPath(), $"garagewatch-test-{Guid.NewGuid():N}.onnx");
        await File.WriteAllBytesAsync(modelPath, OnnxContractFixture.CreateConstantDetectionModel());
        try
        {
            using var detector = new OnnxObjectDetector(new OnnxDetectorOptions(modelPath, "fixture-v1", "images",
                "detections", 2, 2, .25f, ["person"], 1, 1, 1, 1));
            var detections = await detector.DetectAsync(Frame("epoch-a", "frame-onnx"), CancellationToken.None);

            var detection = Assert.Single(detections);
            Assert.Equal("person", detection.ClassName);
            Assert.Equal(.95f, detection.Score, 3);
            Assert.Equal(new DetectionBox(.1f, .2f, .8f, .9f), detection.Box);
        }
        finally { File.Delete(modelPath); }
    }

    [Fact]
    public async Task Detection_worker_decodes_synthetic_segment_and_runs_onnx_without_capture_coupling()
    {
        var root = Path.Combine(Path.GetTempPath(), $"garagewatch-analysis-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var mediaPath = Path.Combine(root, "synthetic.ts");
        var modelPath = Path.Combine(root, "contract.onnx");
        await File.WriteAllBytesAsync(modelPath, OnnxContractFixture.CreateConstantDetectionModel());
        await RunFfmpegAsync(mediaPath);
        var options = new OnnxDetectorOptions(modelPath, "fixture-v1", "images", "detections", 2, 2, .25f,
            ["person"], 1, 1, 1, 1);
        using var detector = new OnnxObjectDetector(options);
        var log = new RecordingAnalysisLogger();
        var worker = new ObjectDetectionWorker(new CaptureOptions("synthetic", "camera-1", root, 1, 10,
                1_048_576, null, "ffmpeg", "ffprobe"), options,
            new DetectionProcessor(detector, new GreedyIouObjectTracker()), log);
        try
        {
            Assert.True(worker.TryEnqueue(new SegmentManifestV1("1.0", "synthetic-segment", "camera-1", "epoch-a",
                DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow, "synthetic.ts", new FileInfo(mediaPath).Length,
                "synthetic-checksum"), mediaPath));
            await worker.StartAsync(CancellationToken.None);
            var message = await log.AnalysisCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("1 detecções", message, StringComparison.Ordinal);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_model_degrades_analysis_but_synthetic_capture_keeps_persisting_segments()
    {
        var root = Path.Combine(Path.GetTempPath(), $"garagewatch-isolation-{Guid.NewGuid():N}");
        var options = new CaptureOptions("synthetic", "camera-1", Path.Combine(root, "capture"), 1, 6,
            4_194_304, null, "ffmpeg", "ffprobe");
        var detectorOptions = new OnnxDetectorOptions(Path.Combine(root, "missing.onnx"), "missing-v1", "images",
            "detections", 32, 32, .25f, ["person"], 1, 1, 1, 1);
        using var detector = new OnnxObjectDetector(detectorOptions);
        var detectionWorker = new ObjectDetectionWorker(options, detectorOptions,
            new DetectionProcessor(detector, new GreedyIouObjectTracker()), NullLogger<ObjectDetectionWorker>.Instance);
        var captureWorker = new CaptureWorker(options, new SyntheticSegmentCaptureAdapter(options),
            new LocalSegmentSpool(options), NullLogger<CaptureWorker>.Instance, detectionWorker: detectionWorker);
        try
        {
            await detectionWorker.StartAsync(CancellationToken.None);
            await captureWorker.StartAsync(CancellationToken.None);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (captureWorker.Status.Segments == 0 && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(100);

            Assert.True(captureWorker.Status.Segments > 0);
            Assert.Equal("capturing", captureWorker.Status.State);
        }
        finally
        {
            await captureWorker.StopAsync(CancellationToken.None);
            await detectionWorker.StopAsync(CancellationToken.None);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static DetectionFrame Frame(string epoch, string id, string camera = "camera-1") =>
        new(camera, epoch, id, DateTimeOffset.UtcNow, 2, 2, new float[12]);

    private sealed class ContractDetector : IObjectDetector
    {
        public int Calls { get; private set; }
        public string ModelVersion => "contract-fixture-v1";

        public ValueTask<IReadOnlyList<ObjectDetection>> DetectAsync(DetectionFrame frame, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult<IReadOnlyList<ObjectDetection>>([
                new ObjectDetection("person", 0, .9f, new DetectionBox(.1f, .1f, .4f, .8f)),
                new ObjectDetection("person", 0, float.NaN, new DetectionBox(.1f, .1f, .4f, .8f)),
                new ObjectDetection("person", 0, .9f, new DetectionBox(-.1f, .1f, .4f, .8f))
            ]);
        }
    }

    private static class OnnxContractFixture
    {
        public static byte[] CreateConstantDetectionModel()
        {
            var tensor = new List<byte>();
            tensor.AddRange(VarintField(1, 1));
            tensor.AddRange(VarintField(1, 6));
            tensor.AddRange(VarintField(2, 1));
            var raw = new byte[24];
            var values = new[] { .1f, .2f, .8f, .9f, .95f, 0f };
            for (var index = 0; index < values.Length; index++)
                System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(index * 4, 4), values[index]);
            tensor.AddRange(BytesField(9, raw));

            var attribute = new List<byte>();
            attribute.AddRange(BytesField(1, "value"u8.ToArray()));
            attribute.AddRange(BytesField(5, tensor.ToArray()));
            attribute.AddRange(VarintField(20, 4));

            var node = new List<byte>();
            node.AddRange(BytesField(2, "detections"u8.ToArray()));
            node.AddRange(BytesField(4, "Constant"u8.ToArray()));
            node.AddRange(BytesField(5, attribute.ToArray()));

            var graph = new List<byte>();
            graph.AddRange(BytesField(1, node.ToArray()));
            graph.AddRange(BytesField(2, "detection_fixture"u8.ToArray()));
            graph.AddRange(BytesField(11, ValueInfo("images", [1, 3, 2, 2])));
            graph.AddRange(BytesField(12, ValueInfo("detections", [1, 6])));

            var opset = VarintField(2, 13);
            var model = new List<byte>();
            model.AddRange(VarintField(1, 9));
            model.AddRange(BytesField(2, "garage-watch-tests"u8.ToArray()));
            model.AddRange(BytesField(7, graph.ToArray()));
            model.AddRange(BytesField(8, opset));
            return model.ToArray();
        }

        private static byte[] ValueInfo(string name, int[] dimensions)
        {
            var shape = new List<byte>();
            foreach (var dimension in dimensions)
            {
                var dim = VarintField(1, (ulong)dimension);
                shape.AddRange(BytesField(1, dim));
            }
            var tensorType = new List<byte>();
            tensorType.AddRange(VarintField(1, 1));
            tensorType.AddRange(BytesField(2, shape.ToArray()));
            var type = BytesField(1, tensorType.ToArray());
            var info = new List<byte>();
            info.AddRange(BytesField(1, System.Text.Encoding.UTF8.GetBytes(name)));
            info.AddRange(BytesField(2, type));
            return info.ToArray();
        }

        private static byte[] VarintField(int field, ulong value)
        {
            var bytes = new List<byte>();
            WriteVarint(bytes, (ulong)(field << 3));
            WriteVarint(bytes, value);
            return bytes.ToArray();
        }

        private static byte[] BytesField(int field, byte[] value)
        {
            var bytes = new List<byte>();
            WriteVarint(bytes, (ulong)(field << 3 | 2));
            WriteVarint(bytes, (ulong)value.Length);
            bytes.AddRange(value);
            return bytes.ToArray();
        }

        private static void WriteVarint(List<byte> bytes, ulong value)
        {
            while (value >= 0x80)
            {
                bytes.Add((byte)(value | 0x80));
                value >>= 7;
            }
            bytes.Add((byte)value);
        }
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
                     "color=c=black:s=32x24:r=1", "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-f", "mpegts", destination })
            start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("ffmpeg_start_failed");
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var error = await stderr;
        Assert.True(process.ExitCode == 0, error);
    }

    private sealed class RecordingAnalysisLogger : ILogger<ObjectDetectionWorker>
    {
        public TaskCompletionSource<string> AnalysisCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message.Contains("Amostra de análise concluída", StringComparison.Ordinal))
                AnalysisCompleted.TrySetResult(message);
        }
    }
}
