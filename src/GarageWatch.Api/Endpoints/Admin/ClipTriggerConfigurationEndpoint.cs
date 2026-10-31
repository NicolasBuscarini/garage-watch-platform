using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class GetClipTriggerConfigurationEndpoint() : BaseEndpoint("Clip trigger", "GetClipTriggerConfiguration",
    "Get camera clip trigger configuration", "Returns the persisted trigger toggle for an authorized administrator.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/cameras/{cameraId}/clip-trigger", async (string cameraId,
            CameraClipTriggerConfigurationService configuration, CancellationToken cancellationToken) =>
        {
            try { return Results.Ok(await configuration.GetAsync(cameraId, cancellationToken)); }
            catch (KeyNotFoundException) { return Results.NotFound(new ApiErrorResponse("camera_not_found")); }
        }).RequireAuthorization("Administrator").Produces<CameraClipTriggerConfiguration>()
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound).Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
}

public sealed class UpdateClipTriggerConfigurationEndpoint() : BaseEndpoint("Clip trigger", "UpdateClipTriggerConfiguration",
    "Update camera clip trigger configuration", "Persists a version-checked per-camera trigger toggle immediately.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut("/api/v1/admin/cameras/{cameraId}/clip-trigger", async (string cameraId,
            UpdateClipTriggerConfigurationRequest request, HttpContext context, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, CameraClipTriggerConfigurationService configuration,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try
            {
                return Results.Ok(await configuration.UpdateAsync(actor.Id, cameraId, request.ExpectedVersion,
                request.Enabled, cancellationToken));
            }
            catch (ArgumentException) { return Results.BadRequest(new ApiErrorResponse("invalid_clip_trigger_configuration")); }
            catch (KeyNotFoundException) { return Results.NotFound(new ApiErrorResponse("camera_not_found")); }
            catch (ClipTriggerConfigurationConflictException) { return Results.Conflict(new ApiErrorResponse("clip_trigger_version_conflict")); }
        }).RequireAuthorization("Administrator").Produces<CameraClipTriggerConfiguration>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest).Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict).Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
}
