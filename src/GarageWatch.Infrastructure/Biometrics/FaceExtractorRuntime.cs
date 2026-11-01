using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Biometrics;

/// <summary>
/// Startup verification of the configured face artifacts. Any missing file, unreadable path or hash
/// divergence keeps enrollment disabled (fail closed) and is reported without exposing paths or secret data.
/// </summary>
public sealed class FaceExtractorRuntime
{
    public const string ReadyState = "ready";
    public const string NotConfiguredState = "not_configured";
    public const string InvalidConfigurationState = "invalid_configuration";
    public const string ArtifactUnavailableState = "artifact_unavailable";
    public const string ChecksumMismatchState = "artifact_checksum_mismatch";

    public FaceExtractorRuntime(FaceExtractorOptions options, ILogger<FaceExtractorRuntime> logger)
    {
        Options = options;
        var state = Verify(out var detectorVerified, out var recognizerVerified);
        Status = new FaceExtractorRuntimeStatus(state == ReadyState, state, "onnxruntime-cpu",
            options.ModelId, options.ModelVersion, detectorVerified, recognizerVerified);
        if (Status.Enabled)
            logger.LogInformation(
                "Extração facial ativa: adapter={Adapter} model={ModelId}/{ModelVersion} detectorSha256={DetectorSha} recognizerSha256={RecognizerSha}",
                Status.AdapterId, Status.ModelId, Status.ModelVersion, Status.DetectorSha256, Status.RecognizerSha256);
        else
            logger.LogWarning("Extração facial indisponível; cadastro biométrico seguirá em falha fechada. state={State}",
                state);
    }

    public FaceExtractorOptions Options { get; }
    public FaceExtractorRuntimeStatus Status { get; }

    private string Verify(out string? detectorVerified, out string? recognizerVerified)
    {
        detectorVerified = null;
        recognizerVerified = null;
        if (Options.ConfigurationError is { } error) return error == "invalid_face_artifact_configuration"
            ? InvalidConfigurationState
            : Options.ConfigurationError;
        if (!Options.Configured) return NotConfiguredState;
        if (!File.Exists(Options.DetectorModelPath!) || !File.Exists(Options.RecognizerModelPath!))
            return ArtifactUnavailableState;
        try
        {
            detectorVerified = Hash(Options.DetectorModelPath!);
            recognizerVerified = Hash(Options.RecognizerModelPath!);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ArtifactUnavailableState;
        }
        if (!string.Equals(detectorVerified, Options.DetectorSha256, StringComparison.Ordinal) ||
            !string.Equals(recognizerVerified, Options.RecognizerSha256, StringComparison.Ordinal))
            return ChecksumMismatchState;
        return ReadyState;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>Sanitized status published in health/registry: no filesystem paths, no biometric data.</summary>
public sealed record FaceExtractorRuntimeStatus(bool Enabled, string State, string AdapterId, string ModelId,
    string ModelVersion, string? DetectorSha256, string? RecognizerSha256);
