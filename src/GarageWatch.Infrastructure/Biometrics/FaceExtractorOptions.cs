using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Biometrics;

/// <summary>
/// Artifact and threshold configuration for the real face extractor (YuNet detector + SFace recognizer).
/// Artifacts are supplied by the operator, mounted read-only and verified by SHA-256 at startup; the
/// service never downloads weights and never persists images or embeddings in plaintext.
/// </summary>
public sealed record FaceExtractorOptions(
    string ModelId,
    string ModelVersion,
    string? DetectorModelPath,
    string? DetectorSha256,
    string? RecognizerModelPath,
    string? RecognizerSha256,
    float ScoreThreshold,
    float NmsThreshold,
    int MinimumFacePixels,
    int MaximumInputSize,
    int IntraOpThreads,
    int TimeoutSeconds,
    string? ConfigurationError = null)
{
    public bool Configured => DetectorModelPath is not null && RecognizerModelPath is not null;

    public static FaceExtractorOptions FromEnvironment(IConfiguration configuration)
    {
        var detectorPath = Normalize(configuration["FACE_DETECTOR_MODEL_PATH"]);
        var detectorHash = NormalizeHash(configuration["FACE_DETECTOR_MODEL_SHA256"]);
        var recognizerPath = Normalize(configuration["FACE_RECOGNIZER_MODEL_PATH"]);
        var recognizerHash = NormalizeHash(configuration["FACE_RECOGNIZER_MODEL_SHA256"]);
        var modelVersion = Normalize(configuration["FACE_EXTRACTOR_MODEL_VERSION"]) ?? "2021dec";
        var score = ParseFloat(configuration["FACE_DETECTOR_SCORE_THRESHOLD"], 0.9f, 0.1f, 0.99f);
        var nms = ParseFloat(configuration["FACE_DETECTOR_NMS_THRESHOLD"], 0.3f, 0.1f, 0.9f);
        var minimumPixels = ParseInt(configuration["FACE_MINIMUM_PIXELS"], 64, 16, 512);
        var maximumInput = ParseInt(configuration["FACE_MAXIMUM_INPUT_SIZE"], 640, 64, 1280);
        var threads = ParseInt(configuration["FACE_INTRA_OP_THREADS"], 1, 1, 8);
        var timeout = ParseInt(configuration["FACE_EXTRACTOR_TIMEOUT_SECONDS"], 20, 5, 120);

        var nothingConfigured = detectorPath is null && detectorHash is null && recognizerPath is null &&
                                recognizerHash is null;
        string? error = null;
        if (!nothingConfigured)
        {
            if (detectorPath is null || detectorHash is null || recognizerPath is null || recognizerHash is null)
                error = "incomplete_face_artifact_configuration";
            else if (!Path.IsPathFullyQualified(detectorPath) || !Path.IsPathFullyQualified(recognizerPath))
                error = "face_artifact_path_not_absolute";
        }

        return new FaceExtractorOptions("opencv-zoo-sface", modelVersion, detectorPath, detectorHash, recognizerPath,
            recognizerHash, score, nms, minimumPixels, maximumInput, threads, timeout, error);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeHash(string? value)
    {
        var normalized = Normalize(value)?.ToLowerInvariant();
        return normalized is { Length: 64 } && normalized.All(Uri.IsHexDigit) ? normalized : null;
    }

    private static float ParseFloat(string? value, float fallback, float minimum, float maximum) =>
        float.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) && float.IsFinite(parsed) &&
        parsed >= minimum && parsed <= maximum
            ? parsed
            : fallback;

    private static int ParseInt(string? value, int fallback, int minimum, int maximum) =>
        int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum ? parsed : fallback;
}
