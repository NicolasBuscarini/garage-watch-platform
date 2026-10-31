using System.Security.Claims;
using GarageWatch.Api.Errors;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Clips;

public sealed class GetClipAnalysisDispatchPolicyEndpoint() : BaseEndpoint("Clip analysis", "GetClipAnalysisDispatchPolicy",
    "Get camera clip analysis dispatch policy", "Returns the policy and scoped analysis capabilities to a camera clip reader.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/cameras/{cameraId}/clip-analysis-dispatch-policy", async (string cameraId,
            ClaimsPrincipal principal, UserManager<ApplicationUser> users, ClipAnalysisDispatchService dispatch,
            CancellationToken cancellationToken) =>
        {
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try { return Results.Ok(await dispatch.GetPolicyAsync(actor.Id, cameraId, cancellationToken)); }
            catch (KeyNotFoundException) { return Results.NotFound(new ApiErrorResponse("analysis_resource_not_found")); }
        }).RequireAuthorization()
            .Produces<ClipAnalysisPolicyView>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
