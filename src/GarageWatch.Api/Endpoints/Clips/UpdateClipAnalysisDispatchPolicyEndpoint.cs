using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Clips;

public sealed class UpdateClipAnalysisDispatchPolicyEndpoint() : BaseEndpoint("Clip analysis", "UpdateClipAnalysisDispatchPolicy",
    "Version clip analysis dispatch policy", "Changes the authorized camera policy without reclassifying accepted jobs; never blocks every automatic and manual request.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut("/api/v1/cameras/{cameraId}/clip-analysis-dispatch-policy", async (string cameraId,
            UpdateClipAnalysisDispatchPolicyRequest request, HttpContext context, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, ClipAnalysisDispatchService dispatch, CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try
            {
                return Results.Ok(await dispatch.UpdatePolicyAsync(actor.Id, cameraId, request.ExpectedVersion,
                    request.Policy, cancellationToken));
            }
            catch (ArgumentException) { return Results.BadRequest(new ApiErrorResponse("invalid_analysis_policy")); }
            catch (KeyNotFoundException) { return Results.NotFound(new ApiErrorResponse("analysis_resource_not_found")); }
            catch (ClipAnalysisConflictException) { return Results.Conflict(new ApiErrorResponse("policy_version_conflict")); }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        }).RequireAuthorization()
            .Produces<ClipAnalysisPolicyResult>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
