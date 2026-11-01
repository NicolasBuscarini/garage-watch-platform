using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GarageWatch.Infrastructure.Identity;

public interface IHasPublicId
{
    string? PublicId { get; set; }
}

public interface IPublicIdGenerator
{
    string Generate(string prefix);
}

public static class PublicIdFormat
{
    public static bool IsValid(string? value, string prefix) => value is not null &&
        value.Length == prefix.Length + 12 && value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
}

public sealed class PublicIdGenerator : IPublicIdGenerator
{
    private const ulong NamespaceSize = 1_000_000_000_000;
    private const ulong RejectionLimit = ulong.MaxValue - (ulong.MaxValue % NamespaceSize);

    public string Generate(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length is < 2 or > 4 ||
            prefix.Any(character => character is < 'A' or > 'Z'))
            throw new ArgumentException("Namespace de ID público inválido.", nameof(prefix));

        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        } while (value >= RejectionLimit);

        return string.Concat(prefix, (value % NamespaceSize).ToString("D12", System.Globalization.CultureInfo.InvariantCulture));
    }
}

public static class PublicIdAllocator
{
    private const int MaximumAttempts = 3;

    public static string GenerateUnique(IPublicIdGenerator generator, string prefix, ISet<string> reservedIds)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(reservedIds);
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var candidate = generator.Generate(prefix);
            if (!PublicIdFormat.IsValid(candidate, prefix))
                throw new InvalidOperationException("O gerador retornou um ID público inválido.");
            if (reservedIds.Add(candidate)) return candidate;
        }
        throw new InvalidOperationException("Não foi possível reservar um ID público após tentativas limitadas.");
    }
}

public static class PublicIdPrefixes
{
    public const string UserPrefix = "USR";
    public const string ContextPrefix = "CTX";
    public const string CameraPrefix = "CAM";
    public const string AccessProfilePrefix = "PRF";
    public const string AccessProfileVersionPrefix = "PVN";
    public const string ProfileAssignmentPrefix = "ASN";
    public const string AccessRequestPrefix = "ARQ";
    public const string ClipPrefix = "CLP";
    public const string ClipAnalysisJobPrefix = "JOB";
    public const string KnownPersonPrefix = "PER";
    public const string FaceExtractorPrefix = "FEX";
    public const string BiometricEnrollmentPrefix = "BIO";

    public static string For(IHasPublicId entity) => entity switch
    {
        ApplicationUser => UserPrefix,
        CameraContext => ContextPrefix,
        CameraCatalogEntry => CameraPrefix,
        AccessProfile => AccessProfilePrefix,
        AccessProfileVersion => AccessProfileVersionPrefix,
        UserAccessProfileAssignment => ProfileAssignmentPrefix,
        AccessRequest => AccessRequestPrefix,
        ClipAnalysisJob => ClipAnalysisJobPrefix,
        KnownPerson => KnownPersonPrefix,
        FaceExtractorCandidate => FaceExtractorPrefix,
        BiometricEnrollment => BiometricEnrollmentPrefix,
        _ => throw new InvalidOperationException("Namespace de ID público não registrado.")
    };
}
