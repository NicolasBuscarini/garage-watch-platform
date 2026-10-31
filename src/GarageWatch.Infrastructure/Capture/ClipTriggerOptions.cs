using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Capture;

public sealed record ClipTriggerOptions(bool Enabled, int SampleFps, int Width, int Height,
    int PixelDelta, double ChangedAreaRatio, int CooldownSeconds, int PreEventSeconds,
    int PostEventSeconds, int MaximumClipSeconds, int QueueCapacity)
{
    public static ClipTriggerOptions FromEnvironment(IConfiguration configuration)
    {
        static int Number(IConfiguration c, string key, int fallback, int min, int max)
        {
            var raw = c[key];
            if (raw is null) return fallback;
            if (!int.TryParse(raw, out var value) || value < min || value > max)
                throw new InvalidOperationException($"{key} fora do limite permitido.");
            return value;
        }
        static double Ratio(IConfiguration c, string key, double fallback)
        {
            var raw = c[key];
            if (raw is null) return fallback;
            if (!double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    out var value) || value is <= 0 or > 1)
                throw new InvalidOperationException($"{key} fora do limite permitido.");
            return value;
        }

        if (!bool.TryParse(configuration["CLIP_TRIGGER_ENABLED"] ?? "false", out var enabled))
            throw new InvalidOperationException("CLIP_TRIGGER_ENABLED deve ser true ou false.");
        return new ClipTriggerOptions(enabled,
            Number(configuration, "CLIP_TRIGGER_SAMPLE_FPS", 1, 1, 5),
            Number(configuration, "CLIP_TRIGGER_WIDTH", 160, 64, 320),
            Number(configuration, "CLIP_TRIGGER_HEIGHT", 90, 64, 180),
            Number(configuration, "CLIP_TRIGGER_PIXEL_DELTA", 24, 1, 255),
            Ratio(configuration, "CLIP_TRIGGER_CHANGED_AREA_RATIO", .05),
            Number(configuration, "CLIP_TRIGGER_COOLDOWN_SECONDS", 10, 0, 3600),
            Number(configuration, "CLIP_PRE_EVENT_SECONDS", 10, 0, 120),
            Number(configuration, "CLIP_POST_EVENT_SECONDS", 20, 0, 120),
            Number(configuration, "CLIP_MAX_SECONDS", 120, 1, 600),
            Number(configuration, "CLIP_TRIGGER_QUEUE_CAPACITY", 8, 1, 64));
    }
}
