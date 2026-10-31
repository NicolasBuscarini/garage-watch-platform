using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Analysis;

public sealed record OnnxDetectorOptions(string? ModelPath, string ModelVersion, string InputName, string OutputName,
    int Width, int Height, float ScoreThreshold, string[] Labels, int QueueCapacity, int IntraOpThreads,
    int InterOpThreads, int SampleEverySegments, string? ConfigurationError = null)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(ModelPath);

    public static OnnxDetectorOptions FromConfiguration(IConfiguration configuration)
    {
        var path = configuration["DETECTOR_ONNX_MODEL_PATH"]?.Trim();
        if (string.IsNullOrEmpty(path)) path = null;
        string? configurationError = null;
        if (path is not null && !Path.IsPathFullyQualified(path))
        {
            path = null;
            configurationError = "model_path_not_absolute";
        }
        var version = configuration["DETECTOR_MODEL_VERSION"]?.Trim() ?? "unconfigured";
        var input = configuration["DETECTOR_ONNX_INPUT_NAME"]?.Trim() ?? "images";
        var output = configuration["DETECTOR_ONNX_OUTPUT_NAME"]?.Trim() ?? "detections";
        var width = ParseInt(configuration["DETECTOR_INPUT_WIDTH"], 640, 32, 2048);
        var height = ParseInt(configuration["DETECTOR_INPUT_HEIGHT"], 640, 32, 2048);
        var threshold = float.TryParse(configuration["DETECTOR_SCORE_THRESHOLD"], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) && float.IsFinite(parsed) && parsed is >= 0 and <= 1
            ? parsed : 0.25f;
        var labels = (configuration["DETECTOR_LABELS"] ?? "person").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (path is not null && (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(input)
            || string.IsNullOrWhiteSpace(output) || labels.Length == 0 || labels.Length > 256 || labels.Any(label => label.Length > 64)))
        {
            path = null;
            configurationError = "invalid_detector_configuration";
        }
        if (string.IsNullOrWhiteSpace(version)) version = "unconfigured";
        if (string.IsNullOrWhiteSpace(input)) input = "images";
        if (string.IsNullOrWhiteSpace(output)) output = "detections";
        if (labels.Length == 0 || labels.Length > 256 || labels.Any(label => label.Length > 64)) labels = ["person"];
        var queueCapacity = ParseInt(configuration["DETECTOR_QUEUE_CAPACITY"], 8, 1, 64);
        var intraOpThreads = ParseInt(configuration["DETECTOR_INTRA_OP_THREADS"], 1, 1, 8);
        var interOpThreads = ParseInt(configuration["DETECTOR_INTER_OP_THREADS"], 1, 1, 4);
        var sampleEverySegments = ParseInt(configuration["DETECTOR_SAMPLE_EVERY_SEGMENTS"], 1, 1, 120);
        return new OnnxDetectorOptions(path, version, input, output, width, height, threshold, labels, queueCapacity,
            intraOpThreads, interOpThreads, sampleEverySegments, configurationError);
    }

    private static int ParseInt(string? value, int fallback, int minimum, int maximum) =>
        int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum ? parsed : fallback;
}
