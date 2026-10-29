using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace GarageWatch.Infrastructure.Capture;

public sealed class FfmpegRtspCaptureAdapter(CaptureOptions options) : ISegmentCaptureAdapter
{
    public string Name => "ffmpeg-rtsp-h264";
    public string Version => "6.1.1-3ubuntu5";
    public IReadOnlyList<string> Capabilities => options.AudioEnabled
        ? ["rtsp-tcp", "h264-validation", "aac-audio", "mpeg-ts-segmentation", "bounded-reconnect"]
        : ["rtsp-tcp", "h264-validation", "mpeg-ts-segmentation", "bounded-reconnect"];

    public async IAsyncEnumerable<CapturedSegment> CaptureAsync(
        string workDirectory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDirectory);
        var output = Path.Combine(workDirectory, "segment_%08d.ts");
        var start = DateTimeOffset.UtcNow;
        var codecValidated = false;
        var startInfo = new ProcessStartInfo(options.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        var arguments = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-rtsp_transport", "tcp", "-timeout", "5000000",
            "-i", options.RtspUrl!, "-map", "0:v:0"
        };
        if (options.AudioEnabled) arguments.AddRange(["-map", "0:a:0?"]);
        arguments.AddRange(["-c:v", "copy"]);
        if (options.AudioEnabled) arguments.AddRange(["-c:a", "aac", "-b:a", "64k", "-ar", "16000", "-ac", "1"]);
        arguments.AddRange(["-f", "segment", "-segment_time", options.SegmentSeconds.ToString(),
            "-segment_format", "mpegts", output]);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("ffmpeg_start_failed");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException("ffmpeg_start_failed", exception);
        }

        var stderrDrain = DrainAsync(process.StandardError);
        var stdoutDrain = DrainAsync(process.StandardOutput);
        try
        {
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var files = Directory.GetFiles(workDirectory, "segment_*.ts").OrderBy(x => x, StringComparer.Ordinal).ToArray();
                if (files.Any(file => new FileInfo(file).Length > options.MaximumSegmentBytes)
                    || files.Sum(file => new FileInfo(file).Length) > options.MaximumSegmentBytes * 2)
                    throw new InvalidDataException("capture_segment_size_limit");
                foreach (var completed in files.Take(Math.Max(0, files.Length - 1)))
                {
                    if (!codecValidated)
                    {
                        await ValidateStreamsAsync(completed, cancellationToken);
                        codecValidated = true;
                    }
                    var end = DateTimeOffset.UtcNow;
                    yield return new CapturedSegment(completed, start, end);
                    start = end;
                }
                await Task.Delay(100, cancellationToken);
            }

            foreach (var completed in Directory.GetFiles(workDirectory, "segment_*.ts").OrderBy(x => x, StringComparer.Ordinal))
            {
                if (new FileInfo(completed).Length > options.MaximumSegmentBytes)
                    throw new InvalidDataException("capture_segment_size_limit");
                if (!codecValidated)
                {
                    await ValidateStreamsAsync(completed, cancellationToken);
                    codecValidated = true;
                }
                var end = DateTimeOffset.UtcNow;
                yield return new CapturedSegment(completed, start, end);
                start = end;
            }

            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stderrDrain, stdoutDrain);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("ffmpeg_stream_ended");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(stderrDrain, stdoutDrain);
        }
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is not null) { }
    }

    private async Task ValidateStreamsAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(options.FfprobePath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-show_entries", "stream=codec_type,codec_name", "-of", "json", path
        }) startInfo.ArgumentList.Add(argument);

        using var probe = new Process { StartInfo = startInfo };
        try
        {
            if (!probe.Start()) throw new InvalidDataException("capture_codec_probe_failed");
            var outputTask = probe.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorDrain = DrainAsync(probe.StandardError);
            await probe.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            await errorDrain;
            if (probe.ExitCode != 0) throw new InvalidDataException("capture_codec_probe_failed");
            using var media = System.Text.Json.JsonDocument.Parse(output);
            var streams = media.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var videoCodecs = streams.Where(stream => stream.GetProperty("codec_type").GetString() == "video")
                .Select(stream => stream.GetProperty("codec_name").GetString()).ToArray();
            var audioCodecs = streams.Where(stream => stream.GetProperty("codec_type").GetString() == "audio")
                .Select(stream => stream.GetProperty("codec_name").GetString()).ToArray();
            if (videoCodecs.Length != 1 || videoCodecs[0] != "h264")
                throw new InvalidDataException("capture_codec_unsupported");
            if (options.AudioEnabled && !audioCodecs.Contains("aac", StringComparer.Ordinal))
                throw new InvalidDataException("capture_audio_unsupported");
        }
        finally
        {
            if (!probe.HasExited)
            {
                probe.Kill(entireProcessTree: true);
                await probe.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}

public sealed class SyntheticSegmentCaptureAdapter(CaptureOptions options) : ISegmentCaptureAdapter
{
    public string Name => "synthetic-segment-source";
    public string Version => "1.0";
    public IReadOnlyList<string> Capabilities => ["synthetic-segments", "gap-contract"];

    public async IAsyncEnumerable<CapturedSegment> CaptureAsync(
        string workDirectory,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDirectory);
        while (!cancellationToken.IsCancellationRequested)
        {
            var start = DateTimeOffset.UtcNow;
            await Task.Delay(TimeSpan.FromSeconds(options.SegmentSeconds), cancellationToken);
            var path = Path.Combine(workDirectory, $"segment_{Guid.NewGuid():N}.ts");
            await File.WriteAllBytesAsync(path, "synthetic-mpegts-contract"u8.ToArray(), cancellationToken);
            yield return new CapturedSegment(path, start, DateTimeOffset.UtcNow);
        }
    }
}
