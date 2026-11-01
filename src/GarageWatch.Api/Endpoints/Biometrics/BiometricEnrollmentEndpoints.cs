using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;
using GarageWatch.Application.Analysis;
using GarageWatch.Application.Biometrics;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Biometrics;

public sealed class ListBiometricEnrollmentsEndpoint() : BaseEndpoint("Biometrics", "ListBiometricEnrollments",
    "List biometric enrollments", "Returns status, expiry and model metadata of encrypted embeddings; it never returns embedding values or images.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/biometrics/enrollments", async (string? userPublicId, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, BiometricEnrollmentService enrollments,
            CancellationToken cancellationToken) =>
        {
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try
            {
                var items = await enrollments.ListAsync(actor.Id, userPublicId, cancellationToken);
                return Results.Ok(items.Select(Map).ToArray());
            }
            catch (KeyNotFoundException) { return Results.NotFound(new ApiErrorResponse("biometric_enrollment_not_found")); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        }).RequireAuthorization()
            .Produces<BiometricEnrollmentResponse[]>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);

    internal static BiometricEnrollmentResponse Map(BiometricEnrollmentSummary item) => new(item.PublicId,
        item.UserPublicId, item.Status, item.EnrolledAtUtc, item.ExpiresAtUtc, item.RevokedAtUtc, item.RevokedReason,
        item.ModelId, item.ModelVersion, item.Dimensions, item.KeyVersion, item.ConsentVersion,
        item.ConsentAcceptedAtUtc);
}

public sealed class EnrollBiometricEndpoint() : BaseEndpoint("Biometrics", "EnrollBiometric",
    "Enroll a face for an account", "Extracts and encrypts one face embedding owned by the target account; an account may enroll itself and privileged managers may support another account.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/biometrics/enrollments", async (EnrollBiometricRequest request, HttpContext context,
            ClaimsPrincipal principal, UserManager<ApplicationUser> users, BiometricEnrollmentService enrollments,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            if (!string.Equals(request.ConsentVersion, BiometricPolicy.ConsentVersion, StringComparison.Ordinal))
                return Results.BadRequest(new ApiErrorResponse("biometric_consent_required"));
            FaceCropV1 crop;
            try
            {
                crop = ToCrop(request);
            }
            catch (FormatException) { return Results.BadRequest(new ApiErrorResponse("invalid_biometric_image")); }
            if (!crop.IsValid) return Results.BadRequest(new ApiErrorResponse("invalid_biometric_image"));
            try
            {
                var created = await enrollments.EnrollAsync(actor.Id, request.UserPublicId, crop, request.ConsentVersion,
                    cancellationToken);
                return Results.Created($"/api/v1/biometrics/enrollments/{created.PublicId}",
                    ListBiometricEnrollmentsEndpoint.Map(created));
            }
            catch (ArgumentException) { return Results.BadRequest(new ApiErrorResponse("invalid_biometric_enrollment")); }
            catch (KeyNotFoundException) { return Results.NotFound(new ApiErrorResponse("biometric_enrollment_not_found")); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            catch (BiometricEnrollmentConflictException)
            {
                return Results.Conflict(new ApiErrorResponse("biometric_enrollment_conflict"));
            }
            catch (BiometricEnrollmentUnavailableException exception)
            {
                return Results.Json(new ApiErrorResponse(exception.Message), statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireAuthorization()
            .Produces<BiometricEnrollmentResponse>(StatusCodes.Status201Created)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);

    private static FaceCropV1 ToCrop(EnrollBiometricRequest request)
    {
        var mediaType = request.MediaType?.Trim();
        if (mediaType is not ("image/jpeg" or "image/png")) throw new FormatException();
        if (string.IsNullOrWhiteSpace(request.ImageBase64)) throw new FormatException();
        var encoded = Convert.FromBase64String(request.ImageBase64);
        return new FaceCropV1(mediaType, request.Width ?? 0, request.Height ?? 0, encoded);
    }
}

public sealed class RevokeBiometricEnrollmentEndpoint() : BaseEndpoint("Biometrics", "RevokeBiometricEnrollment",
    "Revoke a biometric enrollment", "Erases the encrypted embedding and keeps a non-biometric tombstone; allowed for the owning account or a privileged manager.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/api/v1/biometrics/enrollments/{enrollmentId}", async (string enrollmentId, HttpContext context,
            ClaimsPrincipal principal, UserManager<ApplicationUser> users, BiometricEnrollmentService enrollments,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try
            {
                await enrollments.RevokeAsync(actor.Id, enrollmentId, cancellationToken);
                return Results.NoContent();
            }
            catch (KeyNotFoundException) { return Results.NotFound(new ApiErrorResponse("biometric_enrollment_not_found")); }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
        }).RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
