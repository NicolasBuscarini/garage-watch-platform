using GarageWatch.Infrastructure.Capture;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class CapturePipelineTests
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);
    [Fact]
    public void CaptureConfigurationRejectsUnknownProfileAndCredentialedUrlDoesNotEnterDiagnostics()
    {
        var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CAPTURE_PROFILE"] = "both"
        }).Build();
        Assert.Equal("CAPTURE_PROFILE deve selecionar exatamente um perfil suportado.",
            Assert.Throws<InvalidOperationException>(() => CaptureOptions.FromEnvironment(invalid)).Message);

        var invalidUrl = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CAPTURE_PROFILE"] = "ffmpeg-rtsp",
            ["CAPTURE_RTSP_URL"] = "https://user:secret@example.invalid/stream"
        }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => CaptureOptions.FromEnvironment(invalidUrl));
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);

        var authenticatedUrl = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CAPTURE_PROFILE"] = "ffmpeg-rtsp",
            ["CAPTURE_RTSP_URL"] = "rtsp://operator:credential-sentinel@camera.invalid/main"
        }).Build();
        Assert.Equal("rtsp://operator:credential-sentinel@camera.invalid/main",
            CaptureOptions.FromEnvironment(authenticatedUrl).RtspUrl);

        var audioEnabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CAPTURE_PROFILE"] = "ffmpeg-rtsp",
            ["CAPTURE_RTSP_URL"] = "rtsp://camera.invalid/main",
            ["CAPTURE_AUDIO_ENABLED"] = "true"
        }).Build();
        Assert.True(CaptureOptions.FromEnvironment(audioEnabled).AudioEnabled);

        var unsupportedAudio = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CAPTURE_PROFILE"] = "synthetic",
            ["CAPTURE_AUDIO_ENABLED"] = "true"
        }).Build();
        Assert.Equal("CAPTURE_AUDIO_ENABLED só pode ser true no perfil ffmpeg-rtsp.",
            Assert.Throws<InvalidOperationException>(() => CaptureOptions.FromEnvironment(unsupportedAudio)).Message);

        var invalidAudio = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CAPTURE_AUDIO_ENABLED"] = "yes"
        }).Build();
        Assert.Equal("CAPTURE_AUDIO_ENABLED deve ser true ou false.",
            Assert.Throws<InvalidOperationException>(() => CaptureOptions.FromEnvironment(invalidAudio)).Message);

        var defaults = CaptureOptions.FromEnvironment(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["CAPTURE_SEGMENT_SECONDS"] = "5" }).Build());
        Assert.Equal(20, defaults.EffectiveSegmentTimeoutSeconds);
        var invalidTimeout = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CAPTURE_SEGMENT_SECONDS"] = "5",
            ["CAPTURE_SEGMENT_TIMEOUT_SECONDS"] = "5"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => CaptureOptions.FromEnvironment(invalidTimeout));
    }

    [Fact]
    public async Task Synthetic_video_adapter_emits_decodable_h264_with_changing_test_pattern_frames()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-capture-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new CaptureOptions("synthetic", "garage-01", root, 1, 4, 1_048_576, null, "ffmpeg", "ffprobe");
        var adapter = new SyntheticSegmentCaptureAdapter(options);
        var iterator = adapter.CaptureAsync(root, CancellationToken.None).GetAsyncEnumerator();
        try
        {
            Assert.True(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(12)));
            var segment = iterator.Current;
            Assert.True(File.Exists(segment.SourcePath));
            Assert.True(new FileInfo(segment.SourcePath).Length > 1_024);

            var probe = await RunMediaToolAsync("ffprobe", [
                "-v", "error", "-select_streams", "v:0", "-show_entries",
                "stream=codec_name,profile,width,height,pix_fmt", "-of", "json", segment.SourcePath
            ], readBinaryOutput: false);
            using var media = JsonDocument.Parse(probe.TextOutput);
            var stream = Assert.Single(media.RootElement.GetProperty("streams").EnumerateArray());
            Assert.Equal("h264", stream.GetProperty("codec_name").GetString());
            Assert.Equal("Constrained Baseline", stream.GetProperty("profile").GetString());
            Assert.Equal(640, stream.GetProperty("width").GetInt32());
            Assert.Equal(360, stream.GetProperty("height").GetInt32());
            Assert.Equal("yuv420p", stream.GetProperty("pix_fmt").GetString());

            var decoded = await RunMediaToolAsync("ffmpeg", [
                "-v", "error", "-i", segment.SourcePath, "-frames:v", "2", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"
            ], readBinaryOutput: true);
            const int frameBytes = 640 * 360 * 3;
            Assert.True(decoded.BinaryOutput.Length >= frameBytes * 2,
                $"Expected two decoded RGB frames, received {decoded.BinaryOutput.Length} bytes.");
            Assert.False(decoded.BinaryOutput.AsSpan(0, frameBytes).SequenceEqual(decoded.BinaryOutput.AsSpan(frameBytes, frameBytes)),
                "The synthetic source must change across frames, not emit a static/black frame.");
        }
        finally
        {
            await iterator.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Worker_marks_stalled_source_unavailable_then_recovers_and_publishes_a_new_epoch()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-capture-tests", Guid.NewGuid().ToString("N"));
        var options = new CaptureOptions("synthetic", "garage-01", root, 1, 4, 1_048_576, null, "ffmpeg", "ffprobe",
            SegmentTimeoutSeconds: 1);
        var adapter = new StallsOnceThenRecoversAdapter();
        var worker = new CaptureWorker(options, adapter, new LocalSegmentSpool(options),
            NullLogger<CaptureWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var reconnectDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while ((worker.Status.State != "reconnecting" || worker.Status.LastErrorCode != "capture_segment_timeout")
                   && DateTime.UtcNow < reconnectDeadline) await Task.Delay(10);
            Assert.Equal("reconnecting", worker.Status.State);
            Assert.Equal("capture_segment_timeout", worker.Status.LastErrorCode);

            var manifestPath = await WaitForManifestAsync(Path.Combine(root, "manifests"), TimeSpan.FromSeconds(5));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (worker.Status.State != "capturing" && DateTime.UtcNow < deadline) await Task.Delay(10);

            Assert.Equal(2, adapter.Attempts);
            Assert.Equal("capturing", worker.Status.State);
            Assert.NotNull(worker.Status.StreamEpoch);
            var manifest = JsonSerializer.Deserialize<SegmentManifestV1>(await File.ReadAllTextAsync(manifestPath), JsonOptions);
            Assert.NotNull(manifest);
            Assert.Equal(worker.Status.StreamEpoch, manifest.StreamEpoch);

            var gapPath = Assert.Single(Directory.GetFiles(Path.Combine(root, "gaps"), "*.json"));
            var gap = JsonSerializer.Deserialize<CaptureGapV1>(await File.ReadAllTextAsync(gapPath), JsonOptions);
            Assert.NotNull(gap);
            Assert.Null(gap.PreviousStreamEpoch);
            Assert.Equal("capture_source_unavailable", gap.ReasonCode);
            Assert.Equal(1, gap.RetryAttempt);
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await worker.StopAsync(timeout.Token);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkerPersistsEpochManifestAndRecoveryRejectsCorruption()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-capture-tests", Guid.NewGuid().ToString("N"));
        var options = new CaptureOptions("synthetic", "garage-01", root, 1, 4, 1_048_576, null, "ffmpeg", "ffprobe");
        var worker = new CaptureWorker(options, new OneSegmentThenWaitAdapter(), new LocalSegmentSpool(options),
            NullLogger<CaptureWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var manifestPath = await WaitForManifestAsync(Path.Combine(root, "manifests"), TimeSpan.FromSeconds(5));
            var manifestText = await File.ReadAllTextAsync(manifestPath);
            using var manifestJson = System.Text.Json.JsonDocument.Parse(manifestText);
            var manifest = System.Text.Json.JsonSerializer.Deserialize<SegmentManifestV1>(manifestText, JsonOptions);
            Assert.NotNull(manifest);
            AssertContractProperties(manifestJson, "capture-segment-manifest.v1.schema.json");
            Assert.Equal("1.0", manifest.SchemaVersion);
            Assert.Matches("^[a-f0-9]{32}$", manifest.SegmentId);
            Assert.Equal("garage-01", manifest.CameraId);
            Assert.False(string.IsNullOrWhiteSpace(manifest.StreamEpoch));
            Assert.Equal(1, manifest.SizeBytes);
            Assert.Matches("^[0-9a-f]{64}$", manifest.Sha256);
            Assert.Equal(1, await new LocalSegmentSpool(options).RecoverAsync(CancellationToken.None));
            var gapDirectory = Path.Combine(root, "gaps");
            var gapPath = await WaitForManifestAsync(gapDirectory, TimeSpan.FromSeconds(5));
            var gapText = await File.ReadAllTextAsync(gapPath);
            using var gapJson = System.Text.Json.JsonDocument.Parse(gapText);
            var gap = System.Text.Json.JsonSerializer.Deserialize<CaptureGapV1>(gapText, JsonOptions);
            Assert.NotNull(gap);
            AssertContractProperties(gapJson, "capture-gap.v1.schema.json");
            Assert.Matches("^[a-f0-9]{32}$", gap.GapId);
            Assert.Equal("capture_source_unavailable", gap.ReasonCode);
            Assert.Equal(manifest.StreamEpoch, gap.PreviousStreamEpoch);

            await File.WriteAllTextAsync(Path.Combine(root, "segments", manifest.FileName), "x");
            Assert.Equal(0, await new LocalSegmentSpool(options).RecoverAsync(CancellationToken.None));
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await worker.StopAsync(timeout.Token);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Worker_reports_current_retained_segments_after_spool_quota_cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-capture-tests", Guid.NewGuid().ToString("N"));
        var options = new CaptureOptions("synthetic", "garage-01", root, 1, 2, 1_048_576, null, "ffmpeg", "ffprobe");
        var adapter = new FiveSegmentsThenWaitAdapter();
        var worker = new CaptureWorker(options, adapter, new LocalSegmentSpool(options),
            NullLogger<CaptureWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await adapter.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, worker.Status.Segments);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "manifests"), "*.json").Length);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "segments"), "*.ts").Length);
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await worker.StopAsync(timeout.Token);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Initial_source_failure_does_not_claim_an_unobserved_previous_epoch()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-capture-tests", Guid.NewGuid().ToString("N"));
        var options = new CaptureOptions("synthetic", "garage-01", root, 1, 4, 1_048_576, null, "ffmpeg", "ffprobe");
        var worker = new CaptureWorker(options, new FailingCaptureAdapter(), new LocalSegmentSpool(options),
            NullLogger<CaptureWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var gapPath = await WaitForManifestAsync(Path.Combine(root, "gaps"), TimeSpan.FromSeconds(5));
            var gap = System.Text.Json.JsonSerializer.Deserialize<CaptureGapV1>(await File.ReadAllTextAsync(gapPath), JsonOptions);
            Assert.NotNull(gap);
            Assert.Null(gap.PreviousStreamEpoch);
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await worker.StopAsync(timeout.Token);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Worker_reports_retained_gap_count_after_gap_quota_cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-capture-tests", Guid.NewGuid().ToString("N"));
        var options = new CaptureOptions("synthetic", "garage-01", root, 1, 2, 1_048_576, null, "ffmpeg", "ffprobe");
        var adapter = new ThreeFailuresThenWaitAdapter();
        var spool = new LocalSegmentSpool(options);
        var worker = new CaptureWorker(options, adapter, spool, NullLogger<CaptureWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await adapter.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, spool.CountGaps());
            Assert.Equal(2, worker.Status.Gaps);
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await worker.StopAsync(timeout.Token);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Gap_spool_failure_keeps_worker_alive_and_degraded()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-capture-tests", Guid.NewGuid().ToString("N"));
        var options = new CaptureOptions("synthetic", "garage-01", root, 1, 4, 1_048_576, null, "ffmpeg", "ffprobe");
        var worker = new CaptureWorker(options, new GapSpoolFailureAdapter(root), new LocalSegmentSpool(options),
            NullLogger<CaptureWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (worker.Status.State != "spool-unavailable" && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.Equal("spool-unavailable", worker.Status.State);
            Assert.Equal("local_spool_unavailable", worker.Status.LastErrorCode);
            Assert.False(worker.ExecuteTask!.IsCompleted);
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await worker.StopAsync(timeout.Token);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> WaitForManifestAsync(string root, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var manifests = Directory.Exists(root) ? Directory.GetFiles(root, "*.json") : [];
            if (manifests.Length > 0) return manifests[0];
            await Task.Delay(25);
        }
        throw new TimeoutException("Capture worker did not persist a manifest.");
    }

    private static async Task<(string TextOutput, byte[] BinaryOutput)> RunMediaToolAsync(string executable,
        IReadOnlyList<string> arguments, bool readBinaryOutput)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = !readBinaryOutput,
            CreateNoWindow = true
        };
        if (readBinaryOutput) startInfo.RedirectStandardOutput = true;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        using var binary = new MemoryStream();
        Task<string>? textTask = readBinaryOutput ? null : process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task? binaryTask = readBinaryOutput ? process.StandardOutput.BaseStream.CopyToAsync(binary, timeout.Token) : null;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            if (binaryTask is not null) await binaryTask;
            var errors = await errorTask;
            var text = textTask is null ? string.Empty : await textTask;
            Assert.True(process.ExitCode == 0, $"{executable} failed with exit code {process.ExitCode}: {errors}");
            return (text, binary.ToArray());
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
    }

    private static void AssertContractProperties(System.Text.Json.JsonDocument payload, string schemaFile)
    {
        using var schema = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "contracts", schemaFile)));
        var required = schema.RootElement.GetProperty("required").EnumerateArray()
            .Select(element => element.GetString() ?? throw new InvalidOperationException("Schema required field cannot be null."))
            .ToHashSet(StringComparer.Ordinal);
        var schemaProperties = schema.RootElement.GetProperty("properties").EnumerateObject()
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var actualProperties = payload.RootElement.EnumerateObject()
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema.RootElement.GetProperty("$schema").GetString());
        Assert.True(required.SetEquals(schemaProperties));
        Assert.True(required.SetEquals(actualProperties));
    }

    private sealed class OneSegmentThenWaitAdapter : ISegmentCaptureAdapter
    {
        public string Name => "contract-test-source";
        public string Version => "1.0";
        public IReadOnlyList<string> Capabilities => ["contract-test"];

        public async IAsyncEnumerable<CapturedSegment> CaptureAsync(string workDirectory,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(workDirectory);
            var path = Path.Combine(workDirectory, "contract.ts");
            await File.WriteAllBytesAsync(path, [0x47], cancellationToken);
            yield return new CapturedSegment(path, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow);
            throw new InvalidOperationException("contract_source_dropped");
        }
    }

    private sealed class FiveSegmentsThenWaitAdapter : ISegmentCaptureAdapter
    {
        public TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "contract-quota-source";
        public string Version => "1.0";
        public IReadOnlyList<string> Capabilities => ["contract-test"];

        public async IAsyncEnumerable<CapturedSegment> CaptureAsync(string workDirectory,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(workDirectory);
            for (var index = 0; index < 5; index++)
            {
                var path = Path.Combine(workDirectory, $"contract-{index}.ts");
                await File.WriteAllBytesAsync(path, [(byte)index, 0x47], cancellationToken);
                var endedAt = DateTimeOffset.UtcNow.AddSeconds(index);
                yield return new CapturedSegment(path, endedAt.AddSeconds(-1), endedAt);
            }
            Completed.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FailingCaptureAdapter : ISegmentCaptureAdapter
    {
        public string Name => "contract-failure-source";
        public string Version => "1.0";
        public IReadOnlyList<string> Capabilities => ["contract-test"];

        public async IAsyncEnumerable<CapturedSegment> CaptureAsync(string workDirectory,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (cancellationToken.IsCancellationRequested) yield break;
            throw new InvalidOperationException("contract_source_unavailable");
        }
    }

    private sealed class StallsOnceThenRecoversAdapter : ISegmentCaptureAdapter
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public string Name => "contract-stalled-source";
        public string Version => "1.0";
        public IReadOnlyList<string> Capabilities => ["contract-test", "bounded-reconnect"];

        public async IAsyncEnumerable<CapturedSegment> CaptureAsync(string workDirectory,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }

            Directory.CreateDirectory(workDirectory);
            var path = Path.Combine(workDirectory, "recovered.ts");
            await File.WriteAllBytesAsync(path, [0x47, 0x01], cancellationToken);
            var endedAt = DateTimeOffset.UtcNow;
            yield return new CapturedSegment(path, endedAt.AddSeconds(-1), endedAt);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ThreeFailuresThenWaitAdapter : ISegmentCaptureAdapter
    {
        private int _failures;
        public TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "contract-gap-quota-source";
        public string Version => "1.0";
        public IReadOnlyList<string> Capabilities => ["contract-test"];

        public async IAsyncEnumerable<CapturedSegment> CaptureAsync(string workDirectory,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (Interlocked.Increment(ref _failures) <= 3)
                throw new InvalidOperationException("contract_source_unavailable");

            Completed.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class GapSpoolFailureAdapter(string spoolDirectory) : ISegmentCaptureAdapter
    {
        public string Name => "contract-gap-spool-failure-source";
        public string Version => "1.0";
        public IReadOnlyList<string> Capabilities => ["contract-test"];

        public async IAsyncEnumerable<CapturedSegment> CaptureAsync(string workDirectory,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (cancellationToken.IsCancellationRequested) yield break;
            var gapDirectory = Path.Combine(spoolDirectory, "gaps");
            Directory.Delete(gapDirectory);
            await File.WriteAllTextAsync(gapDirectory, "not-a-directory", cancellationToken);
            throw new InvalidOperationException("contract_source_unavailable");
        }
    }
}
