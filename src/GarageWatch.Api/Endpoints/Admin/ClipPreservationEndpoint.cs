using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class UpdateClipPreservationEndpoint() : BaseEndpoint("Clip preservation", "UpdateClipPreservation",
    "Preserve or release a clip", "Sets a durable, audited preservation flag using the public clip identifier.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut("/api/v1/clips/{clipId}/preservation", async (string clipId,
            UpdateClipPreservationRequest request, HttpContext context, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, ClipRetentionService retention, CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try
            {
                return Results.Ok(await retention.SetPreservationAsync(actor.Id, clipId, request.ExpectedVersion,
                    request.IsPreserved, cancellationToken));
            }
            catch (ArgumentException) { return Results.BadRequest(new ApiErrorResponse("invalid_clip_preservation")); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ClipRetentionConflictException exception) { return Results.Conflict(new ApiErrorResponse(exception.Message)); }
        }).RequireAuthorization("Administrator").Produces<ClipPreservationSettings>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
