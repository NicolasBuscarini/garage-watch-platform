using System.Security.Claims;
using GarageWatch.Api.Endpoints;
using GarageWatch.Infrastructure.Identity;

namespace GarageWatch.Api.Endpoints.AccessRequests;

public sealed class GetMyAccessRequestEndpoint() : BaseEndpoint("Access requests", "GetMyAccessRequest",
    "Get current access request", "Returns only the authenticated User's pending access request, if any.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/access-requests/mine", async (ClaimsPrincipal principal, AccessRequestService requests,
            CancellationToken cancellationToken) =>
        {
            var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            return Results.Ok(new { request = await requests.GetPendingAsync(userId, cancellationToken) });
        }).RequireAuthorization("User").Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
