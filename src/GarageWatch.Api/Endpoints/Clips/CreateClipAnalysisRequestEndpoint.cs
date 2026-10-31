using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Clips;

public sealed class CreateClipAnalysisRequestEndpoint() : BaseEndpoint("Clip analysis", "CreateClipAnalysisRequest",
    "Request clip analysis", "Queues one durable logical request for an intact authorized clip and analysis version; policy, scope, integrity and bounded capacity are verified server-side.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/clips/{clipId}/analysis-requests", async (string clipId, CreateClipAnalysisRequest request,
            HttpContext context, ClaimsPrincipal principal, UserManager<ApplicationUser> users,
            ClipAnalysisDispatchService dispatch, CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try
            {
                var result = await dispatch.RequestAsync(actor.Id, clipId, request.IdempotencyKey, request.AnalysisVersion,
                    cancellationToken);
                return Results.Accepted($"/api/v1/clips/{clipId}", result);
            }
            catch (ArgumentException) { return Results.BadRequest(new ApiErrorResponse("invalid_analysis_request")); }
            catch (KeyNotFoundException) { return Results.NotFound(new ApiErrorResponse("clip_not_found")); }
            catch (ClipAnalysisDeniedException) { return Results.Conflict(new ApiErrorResponse("analysis_not_allowed")); }
            catch (ClipAnalysisConflictException) { return Results.Conflict(new ApiErrorResponse("idempotency_conflict")); }
            catch (ClipAnalysisCapacityException) { return Results.Conflict(new ApiErrorResponse("analysis_capacity_reached")); }
            catch (UnauthorizedAccessException) { return Results.NotFound(new ApiErrorResponse("clip_not_found")); }
        }).RequireAuthorization()
            .Produces<ClipAnalysisJobResult>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
