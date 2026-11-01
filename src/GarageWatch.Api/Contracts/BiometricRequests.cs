namespace GarageWatch.Api.Contracts;

public sealed record EnrollBiometricRequest(string? UserPublicId, string? MediaType, int? Width, int? Height,
    string? ImageBase64, string? ConsentVersion);

public sealed record BiometricEnrollmentResponse(string PublicId, string UserPublicId, string Status,
    DateTimeOffset EnrolledAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? RevokedAtUtc, string RevokedReason,
    string ModelId, string ModelVersion, int Dimensions, string KeyVersion, string ConsentVersion,
    DateTimeOffset ConsentAcceptedAtUtc);
