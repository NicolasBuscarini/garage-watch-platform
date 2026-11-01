using System.Security.Cryptography;
using GarageWatch.Application.Biometrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Biometrics;

/// <summary>AES-256-GCM protector for face embeddings; an absent or invalid key disables enrollment instead of storing plaintext.</summary>
public sealed class AesGcmBiometricDataProtector : IBiometricDataProtector
{
    public const string KeySetting = "BIOMETRIC_ENCRYPTION_KEY";
    public const string KeyVersionSetting = "BIOMETRIC_ENCRYPTION_KEY_VERSION";
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[]? _key;
    private readonly ILogger<AesGcmBiometricDataProtector> _logger;

    public AesGcmBiometricDataProtector(IConfiguration configuration, ILogger<AesGcmBiometricDataProtector> logger)
    {
        _logger = logger;
        var configuredKey = configuration[KeySetting]?.Trim();
        var configuredVersion = configuration[KeyVersionSetting]?.Trim();
        ActiveKeyVersion = string.IsNullOrWhiteSpace(configuredVersion) ? null : configuredVersion;
        if (string.IsNullOrEmpty(configuredKey))
            return;
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(configuredKey);
        }
        catch (FormatException)
        {
            _logger.LogError("Chave de biometria inválida: {Setting} não é base64.", KeySetting);
            return;
        }
        if (decoded.Length != KeyBytes)
        {
            _logger.LogError("Chave de biometria inválida: {Setting} deve ter {Bytes} bytes.", KeySetting, KeyBytes);
            return;
        }
        if (ActiveKeyVersion is null)
        {
            _logger.LogError("Chave de biometria inválida: {Setting} ausente.", KeyVersionSetting);
            return;
        }
        _key = decoded;
    }

    public bool IsConfigured => _key is not null && ActiveKeyVersion is not null;

    public string? ActiveKeyVersion { get; }

    public ProtectedBiometricEmbedding Protect(ReadOnlySpan<byte> embeddingFloats)
    {
        if (_key is null || ActiveKeyVersion is null)
            throw new InvalidOperationException("Proteção de biometria não configurada.");
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[embeddingFloats.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, embeddingFloats, ciphertext, tag);
        return new(ciphertext, nonce, tag, ActiveKeyVersion);
    }
}
