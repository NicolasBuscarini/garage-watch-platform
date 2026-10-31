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

    [Fact]
    public async Task Triggered_clip_is_bounded_and_recovers_as_a_single_ready_manifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-clip-tests", Guid.NewGuid().ToString("N"));
        var capture = new CaptureOptions("synthetic", "garage-01", root, 1, 1, 1_048_576, null, "ffmpeg", "ffprobe");
        var triggerOptions = new ClipTriggerOptions(true, 1, 64, 64, 24, .05, 0, 10, 0, 120, 2);
        var spool = new LocalSegmentSpool(capture);
        var trigger = new ClipTriggerWorker(capture, triggerOptions, spool, new SecondSegmentDetector(),
            new ContractClipAssembler(), NullLogger<ClipTriggerWorker>.Instance);
        await trigger.StartAsync(CancellationToken.None);
        try
        {
            var segments = new List<SegmentManifestV1>();
            for (var index = 0; index < 3; index++)
            {
                var sourceDirectory = Path.Combine(root, "input");
                Directory.CreateDirectory(sourceDirectory);
                var sourcePath = Path.Combine(sourceDirectory, $"{index}.ts");
                await File.WriteAllBytesAsync(sourcePath, [(byte)index, 0x47]);
                var endedAt = DateTimeOffset.UtcNow.AddSeconds(index);
                segments.Add(await spool.StoreAsync(new CapturedSegment(sourcePath, endedAt.AddSeconds(-1), endedAt),
                    Guid.NewGuid().ToString(), CancellationToken.None));
            }
            Assert.True(trigger.TryEnqueue(segments[0]));
            Assert.True(trigger.TryEnqueue(segments[1]));
            var path = await WaitForManifestAsync(Path.Combine(root, "clips", "manifests"), TimeSpan.FromSeconds(5));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            ClipManifestV1? clip = null;
            while (DateTime.UtcNow < deadline)
            {
                clip = JsonSerializer.Deserialize<ClipManifestV1>(await File.ReadAllTextAsync(path), JsonOptions);
                if (clip?.State == "ready") break;
                await Task.Delay(25);
            }
            Assert.NotNull(clip);
            Assert.Equal("ready", clip.State);
            Assert.NotEmpty(clip.SegmentIds);
            Assert.NotNull(clip.MediaSha256);
            Assert.Empty(spool.GetPinnedSegmentIds());
            using var payload = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            AssertContractProperties(payload, "clip-manifest.v1.schema.json");
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await trigger.StopAsync(timeout.Token);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Collecting_clip_manifest_pins_only_its_safe_segment_ids()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-clip-tests", Guid.NewGuid().ToString("N"));
        var capture = new CaptureOptions("synthetic", "garage-01", root, 1, 1, 1_048_576, null, "ffmpeg", "ffprobe");
        var spool = new LocalSegmentSpool(capture);
        var id = Guid.NewGuid().ToString("N");
        var other = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(root, "clips", "manifests");
        Directory.CreateDirectory(directory);
        var clip = new ClipManifestV1("1.0", Guid.NewGuid().ToString("N"), "garage-01", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "collecting", "trigger-v1", "visual_change",
            [id, "unsafe"], null, null, null, null, 0, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(Path.Combine(directory, clip.ClipId + ".json"), JsonSerializer.Serialize(clip, JsonOptions));
        try
        {
            Assert.Contains(id, spool.GetPinnedSegmentIds());
            Assert.DoesNotContain("unsafe", spool.GetPinnedSegmentIds());
            Assert.DoesNotContain(other, spool.GetPinnedSegmentIds());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Visual_detector_suppresses_global_change_and_honors_zone_and_masks()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-clip-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var capture = new CaptureOptions("synthetic", "garage-01", root, 1, 4, 1_048_576, null, "ffmpeg", "ffprobe");
        var options = new ClipTriggerOptions(true, 1, 64, 64, 24, .05, 0, 0, 0, 120, 2);
        try
        {
            var black = await CreateColorSegmentAsync(root, "black", "black.ts");
            var white = await CreateColorSegmentAsync(root, "white", "white.ts");
            var local = await CreateColorSegmentAsync(root, "black", "local.ts", "drawbox=x=0:y=0:w=16:h=16:color=white:t=fill");
            var detector = new FfmpegVisualChangeDetector(capture, options);

            Assert.False(await detector.HasRelevantChangeAsync(black, CancellationToken.None));
            Assert.False(await detector.HasRelevantChangeAsync(white, CancellationToken.None));
            var localDetector = new FfmpegVisualChangeDetector(capture, options);
            Assert.False(await localDetector.HasRelevantChangeAsync(black, CancellationToken.None));
            Assert.True(await localDetector.HasRelevantChangeAsync(local, CancellationToken.None));

            var masked = new FfmpegVisualChangeDetector(capture, options with
            {
                Masks = [new NormalizedRectangle(0, 0, .5, .5)]
            });
            Assert.False(await masked.HasRelevantChangeAsync(black, CancellationToken.None));
            Assert.False(await masked.HasRelevantChangeAsync(local, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Visual_detector_applies_pixel_delta_and_changed_area_thresholds()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-clip-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var capture = new CaptureOptions("synthetic", "garage-01", root, 1, 4, 1_048_576, null, "ffmpeg", "ffprobe");
        try
        {
            var black = await CreateColorSegmentAsync(root, "black", "black.ts");
            var local = await CreateColorSegmentAsync(root, "black", "local.ts", "drawbox=x=0:y=0:w=16:h=16:color=white:t=fill");
            var acceptsArea = new ClipTriggerOptions(true, 1, 64, 64, 24, .05, 0, 0, 0, 120, 2);
            var rejectsArea = acceptsArea with { ChangedAreaRatio = .10 };
            var rejectsDelta = acceptsArea with { PixelDelta = 256 };

            var areaDetector = new FfmpegVisualChangeDetector(capture, acceptsArea);
            Assert.False(await areaDetector.HasRelevantChangeAsync(black, CancellationToken.None));
            Assert.True(await areaDetector.HasRelevantChangeAsync(local, CancellationToken.None));

            var strictAreaDetector = new FfmpegVisualChangeDetector(capture, rejectsArea);
            Assert.False(await strictAreaDetector.HasRelevantChangeAsync(black, CancellationToken.None));
            Assert.False(await strictAreaDetector.HasRelevantChangeAsync(local, CancellationToken.None));

            var strictDeltaDetector = new FfmpegVisualChangeDetector(capture, rejectsDelta);
            Assert.False(await strictDeltaDetector.HasRelevantChangeAsync(black, CancellationToken.None));
            Assert.False(await strictDeltaDetector.HasRelevantChangeAsync(local, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Cooldown_suppresses_consecutive_triggered_segments_without_blocking_the_queue()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-clip-tests", Guid.NewGuid().ToString("N"));
        var capture = new CaptureOptions("synthetic", "garage-01", root, 1, 4, 1_048_576, null, "ffmpeg", "ffprobe");
        var options = new ClipTriggerOptions(true, 1, 64, 64, 24, .05, 60, 0, 0, 120, 4);
        var spool = new LocalSegmentSpool(capture);
        Directory.CreateDirectory(Path.Combine(root, "input"));
        var manifests = new List<SegmentManifestV1>();
        for (var index = 0; index < 3; index++)
        {
            var source = Path.Combine(root, "input", $"{index}.ts");
            await File.WriteAllBytesAsync(source, [(byte)index, 0x47]);
            var endedAt = DateTimeOffset.UtcNow.AddSeconds(index);
            manifests.Add(await spool.StoreAsync(new CapturedSegment(source, endedAt.AddSeconds(-1), endedAt),
                Guid.NewGuid().ToString(), CancellationToken.None));
        }
        var worker = new ClipTriggerWorker(capture, options, spool, new AlwaysChangedDetector(), new ContractClipAssembler(),
            NullLogger<ClipTriggerWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.All(manifests, manifest => Assert.True(worker.TryEnqueue(manifest)));
            var directory = Path.Combine(root, "clips", "manifests");
            _ = await WaitForManifestAsync(directory, TimeSpan.FromSeconds(5));
            await Task.Delay(200);
            Assert.Single(Directory.GetFiles(directory, "*.json"));
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await worker.StopAsync(timeout.Token);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Local_assembler_creates_checked_media_and_thumbnail_from_real_segments()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-clip-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var capture = new CaptureOptions("synthetic", "garage-01", root, 1, 4, 1_048_576, null, "ffmpeg", "ffprobe");
        var clip = new ClipManifestV1("1.0", Guid.NewGuid().ToString("N"), "garage-01", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "collecting", "trigger-v1", "test",
            [Guid.NewGuid().ToString("N")], null, null, null, null, 0, DateTimeOffset.UtcNow);
        try
        {
            var segment = await CreateColorSegmentAsync(root, "blue", "input.ts");
            var result = await new LocalClipMediaAssembler(capture).AssembleAsync(clip, [segment], CancellationToken.None);
            var media = Path.Combine(root, "clips", "media", result.MediaFileName);
            var thumbnail = Path.Combine(root, "clips", "thumbnails", result.ThumbnailFileName);

            Assert.True(File.Exists(media));
            Assert.True(File.Exists(thumbnail));
            Assert.True(new FileInfo(thumbnail).Length > 100);
            Assert.Matches("^[a-f0-9]{64}$", result.MediaSha256);
            Assert.Matches("^[a-f0-9]{64}$", result.ThumbnailSha256);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Restart_recovers_collecting_clip_once_when_referenced_segments_remain()
    {
        var root = Path.Combine(Path.GetTempPath(), "garagewatch-clip-tests", Guid.NewGuid().ToString("N"));
        var capture = new CaptureOptions("synthetic", "garage-01", root, 1, 4, 1_048_576, null, "ffmpeg", "ffprobe");
        var options = new ClipTriggerOptions(true, 1, 64, 64, 24, .05, 0, 0, 0, 120, 2);
        var spool = new LocalSegmentSpool(capture);
        Directory.CreateDirectory(Path.Combine(root, "input"));
        var source = Path.Combine(root, "input", "segment.ts");
        await File.WriteAllBytesAsync(source, [0x47, 0x01]);
        var now = DateTimeOffset.UtcNow;
        var segment = await spool.StoreAsync(new CapturedSegment(source, now.AddSeconds(-1), now), Guid.NewGuid().ToString(), CancellationToken.None);
        var clip = new ClipManifestV1("1.0", Guid.NewGuid().ToString("N"), "garage-01", now.AddSeconds(-1), now,
            now.AddSeconds(-1), now, "collecting", "trigger-v1", "recovery", [segment.SegmentId], null, null, null, null, 0, now);
        var directory = Path.Combine(root, "clips", "manifests");
        Directory.CreateDirectory(directory);
        var manifestPath = Path.Combine(directory, clip.ClipId + ".json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(clip, JsonOptions));
        var worker = new ClipTriggerWorker(capture, options, spool, new SecondSegmentDetector(), new ContractClipAssembler(),
            NullLogger<ClipTriggerWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            ClipManifestV1? recovered = null;
            while (DateTime.UtcNow < deadline)
            {
                recovered = JsonSerializer.Deserialize<ClipManifestV1>(await File.ReadAllTextAsync(manifestPath), JsonOptions);
                if (recovered?.State == "ready") break;
                await Task.Delay(25);
            }
            Assert.NotNull(recovered);
            Assert.Equal("ready", recovered.State);
            Assert.Equal(1, recovered.AttemptCount);
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

    private static async Task<string> CreateColorSegmentAsync(string directory, string color, string fileName, string? filter = null)
    {
        var path = Path.Combine(directory, fileName);
        await RunMediaToolAsync("ffmpeg", ["-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            filter is null ? $"color=c={color}:s=64x64:r=5" : $"color=c={color}:s=64x64:r=5,{filter}", "-t", "1", "-an", "-c:v", "libx264", "-preset", "ultrafast",
            "-pix_fmt", "yuv420p", "-f", "mpegts", path], readBinaryOutput: false);
        return path;
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

    private sealed class SecondSegmentDetector : IVisualChangeDetector
    {
        private int calls;
        public Task<bool> HasRelevantChangeAsync(string segmentPath, CancellationToken cancellationToken) =>
            Task.FromResult(Interlocked.Increment(ref calls) == 2);
    }

    private sealed class AlwaysChangedDetector : IVisualChangeDetector
    {
        public Task<bool> HasRelevantChangeAsync(string segmentPath, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class ContractClipAssembler : IClipMediaAssembler
    {
        public Task<(string MediaFileName, string MediaSha256, string ThumbnailFileName, string ThumbnailSha256)> AssembleAsync(
            ClipManifestV1 clip, IReadOnlyList<string> segmentPaths, CancellationToken cancellationToken) =>
            Task.FromResult(($"{clip.ClipId}.ts", new string('a', 64), $"{clip.ClipId}.jpg", new string('b', 64)));
    }

}
