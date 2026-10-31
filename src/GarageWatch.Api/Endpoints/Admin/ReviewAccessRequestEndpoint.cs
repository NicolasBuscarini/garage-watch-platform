using System.Security.Claims;
using GarageWatch.Api.Endpoints;
using GarageWatch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class ReviewAccessRequestEndpoint() : BaseEndpoint("User administration", "AdminReviewAccessRequest",
    "Mark an access request reviewed", "Closes the request for follow-up only; does not grant or revoke permissions.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/admin/access-requests/{requestId}/review", async (string requestId,
            HttpContext context, ClaimsPrincipal principal, AccessRequestService requests,
            IamDbContext database, CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actorId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            try
            {
                if (!PublicIdFormat.IsValid(requestId, PublicIdPrefixes.AccessRequestPrefix))
                    return Results.NotFound(new ApiErrorResponse("access_request_not_found"));
                var internalId = await database.AccessRequests.Where(item => item.PublicId == requestId)
                    .Select(item => (Guid?)item.Id).SingleOrDefaultAsync(cancellationToken);
                if (internalId is null) return Results.NotFound(new ApiErrorResponse("access_request_not_found"));
                await requests.ReviewAsync(actorId, internalId.Value, cancellationToken);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new ApiErrorResponse("access_request_not_found"));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        }).RequireAuthorization("Administrator").Produces(StatusCodes.Status204NoContent)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
