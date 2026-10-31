using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Capture;

public sealed record ClipTriggerOptions(bool Enabled, int SampleFps, int Width, int Height,
    int PixelDelta, double ChangedAreaRatio, int CooldownSeconds, int PreEventSeconds,
    int PostEventSeconds, int MaximumClipSeconds, int QueueCapacity,
    double GlobalChangedAreaRatio = .85, NormalizedRectangle? Zone = null,
    IReadOnlyList<NormalizedRectangle>? Masks = null)
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
        static NormalizedRectangle Rectangle(string raw, string key)
        {
            var values = raw.Split(',').Select(value => double.TryParse(value,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed : double.NaN).ToArray();
            if (values.Length != 4 || values.Any(double.IsNaN) || values[0] < 0 || values[1] < 0 || values[2] <= 0 || values[3] <= 0
                || values[0] + values[2] > 1 || values[1] + values[3] > 1)
                throw new InvalidOperationException($"{key} deve conter x,y,largura,altura normalizados.");
            return new NormalizedRectangle(values[0], values[1], values[2], values[3]);
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
            Number(configuration, "CLIP_TRIGGER_QUEUE_CAPACITY", 8, 1, 64),
            Ratio(configuration, "CLIP_TRIGGER_GLOBAL_CHANGED_AREA_RATIO", .85),
            Rectangle(configuration["CLIP_TRIGGER_ZONE"] ?? "0,0,1,1", "CLIP_TRIGGER_ZONE"),
            (configuration["CLIP_TRIGGER_MASKS"] ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select((value, index) => Rectangle(value, $"CLIP_TRIGGER_MASKS[{index}]")).ToArray());
    }
}

public sealed record NormalizedRectangle(double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}
