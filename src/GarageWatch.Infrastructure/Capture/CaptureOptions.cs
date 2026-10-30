using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Capture;

public sealed record CaptureOptions(
    string Profile,
    string CameraId,
    string SpoolDirectory,
    int SegmentSeconds,
    int MaximumSegments,
    long MaximumSegmentBytes,
    string? RtspUrl,
    string FfmpegPath,
    string FfprobePath,
    bool AudioEnabled = false,
    int SegmentTimeoutSeconds = 0)
{
    public int EffectiveSegmentTimeoutSeconds => SegmentTimeoutSeconds > 0
        ? SegmentTimeoutSeconds
        : Math.Max(15, SegmentSeconds * 3 + 5);

    public static CaptureOptions FromEnvironment(IConfiguration configuration)
    {
        var profile = configuration["CAPTURE_PROFILE"] ?? "synthetic";
        if (profile is not ("synthetic" or "ffmpeg-rtsp"))
        {
            throw new InvalidOperationException("CAPTURE_PROFILE deve selecionar exatamente um perfil suportado.");
        }

        var cameraId = configuration["CAPTURE_CAMERA_ID"] ?? "garage-01";
        if (cameraId.Length is < 1 or > 64 || cameraId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        {
            throw new InvalidOperationException("CAPTURE_CAMERA_ID inválido.");
        }

        var segmentSeconds = ParseBounded(configuration["CAPTURE_SEGMENT_SECONDS"], 5, 1, 60, "CAPTURE_SEGMENT_SECONDS");
        var segmentTimeoutSeconds = ParseBounded(configuration["CAPTURE_SEGMENT_TIMEOUT_SECONDS"], 0, 0, 300,
            "CAPTURE_SEGMENT_TIMEOUT_SECONDS");
        if (segmentTimeoutSeconds > 0 && segmentTimeoutSeconds <= segmentSeconds)
            throw new InvalidOperationException("CAPTURE_SEGMENT_TIMEOUT_SECONDS deve ser maior que CAPTURE_SEGMENT_SECONDS.");
        var maximumSegments = ParseBounded(configuration["CAPTURE_MAX_SEGMENTS"], 120, 1, 10_000, "CAPTURE_MAX_SEGMENTS");
        var maximumSegmentBytes = ParseLongBounded(configuration["CAPTURE_MAX_SEGMENT_BYTES"], 67_108_864, 1_048_576, 1_073_741_824, "CAPTURE_MAX_SEGMENT_BYTES");
        var spoolDirectory = configuration["CAPTURE_SPOOL_DIRECTORY"] ?? "/data/capture";
        if (!Path.IsPathFullyQualified(spoolDirectory))
        {
            throw new InvalidOperationException("CAPTURE_SPOOL_DIRECTORY deve ser absoluto.");
        }

        var rtspUrl = configuration["CAPTURE_RTSP_URL"];
        var audioEnabledText = configuration["CAPTURE_AUDIO_ENABLED"] ?? "false";
        if (!bool.TryParse(audioEnabledText, out var audioEnabled))
        {
            throw new InvalidOperationException("CAPTURE_AUDIO_ENABLED deve ser true ou false.");
        }
        if (profile == "synthetic" && !string.IsNullOrWhiteSpace(rtspUrl))
        {
            throw new InvalidOperationException("CAPTURE_RTSP_URL só pode ser configurada no perfil ffmpeg-rtsp.");
        }
        if (profile == "ffmpeg-rtsp")
        {
            if (!Uri.TryCreate(rtspUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != "rtsp"
                || string.IsNullOrWhiteSpace(uri.Host))
            {
                throw new InvalidOperationException("CAPTURE_RTSP_URL deve ser uma URI RTSP válida.");
            }
        }

        if (audioEnabled && profile != "ffmpeg-rtsp")
        {
            throw new InvalidOperationException("CAPTURE_AUDIO_ENABLED só pode ser true no perfil ffmpeg-rtsp.");
        }

        return new CaptureOptions(profile, cameraId, spoolDirectory, segmentSeconds, maximumSegments, maximumSegmentBytes, rtspUrl,
            configuration["CAPTURE_FFMPEG_PATH"] ?? "ffmpeg",
            configuration["CAPTURE_FFPROBE_PATH"] ?? "ffprobe", audioEnabled, segmentTimeoutSeconds);
    }

    private static int ParseBounded(string? raw, int fallback, int minimum, int maximum, string name)
    {
        if (raw is null) return fallback;
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{name} fora do limite permitido.");
        return value;
    }

    private static long ParseLongBounded(string? raw, long fallback, long minimum, long maximum, string name)
    {
        if (raw is null) return fallback;
        if (!long.TryParse(raw, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{name} fora do limite permitido.");
        return value;
    }
}
