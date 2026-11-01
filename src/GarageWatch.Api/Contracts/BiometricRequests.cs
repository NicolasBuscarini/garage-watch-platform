namespace GarageWatch.Api.Contracts;

public sealed record EnrollBiometricRequest(string? UserPublicId, string? MediaType, int? Width, int? Height,
    string? ImageBase64);

public sealed record BiometricEnrollmentResponse(string PublicId, string UserPublicId, string Status,
    DateTimeOffset EnrolledAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? RevokedAtUtc, string RevokedReason,
    string ModelId, string ModelVersion, int Dimensions, string KeyVersion);
