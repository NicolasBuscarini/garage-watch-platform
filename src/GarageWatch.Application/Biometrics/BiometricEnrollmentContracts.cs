using GarageWatch.Application.Analysis;

namespace GarageWatch.Application.Biometrics;

/// <summary>Fixed, durable retention policy for biometric enrollments decided by the operator (ADR071).</summary>
public static class BiometricPolicy
{
    public const int RetentionDays = 365;
    public const int MaximumImageBytes = FaceCropV1.MaximumEncodedBytes;

    public static TimeSpan Retention => TimeSpan.FromDays(RetentionDays);
}

/// <summary>Encrypted embedding produced by a project-owned protector; plaintext never leaves memory.</summary>
public sealed record ProtectedBiometricEmbedding(byte[] Ciphertext, byte[] Nonce, byte[] Tag, string KeyVersion);

/// <summary>Substitutable boundary for embedding protection; missing keys must fail closed.</summary>
public interface IBiometricDataProtector
{
    bool IsConfigured { get; }
    string? ActiveKeyVersion { get; }
    ProtectedBiometricEmbedding Protect(ReadOnlySpan<byte> embeddingFloats);
}

/// <summary>Substitutable boundary for detecting/aligning a face and producing an embedding from an uploaded image.</summary>
public interface IFaceEnrollmentExtractor
{
    string ModelId { get; }
    string ModelVersion { get; }
    string AdapterId { get; }
    ValueTask<FaceEmbeddingV1> ExtractAsync(FaceCropV1 crop, CancellationToken cancellationToken);
}
