using System.Security.Claims;
using GarageWatch.Api.Endpoints;
using GarageWatch.Infrastructure.Identity;

namespace GarageWatch.Api.Endpoints.AccessRequests;

public sealed record CreateAccessRequestBody(Guid IdempotencyKey, string Purpose);

public sealed class CreateAccessRequestEndpoint() : BaseEndpoint("Access requests", "CreateAccessRequest",
    "Request access", "Creates a durable, deduplicated request for an active User; it never changes permissions or sends email.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/access-requests", async (CreateAccessRequestBody body, HttpContext context,
            ClaimsPrincipal principal, AccessRequestService requests, CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var userId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
            try
            {
                var result = await requests.CreateAsync(userId, body.IdempotencyKey, body.Purpose, cancellationToken);
                return Results.Accepted("/api/v1/access-requests/mine", result);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new ApiErrorResponse("invalid_access_request"));
            }
            catch (AccessRequestConflictException)
            {
                return Results.Conflict(new ApiErrorResponse("idempotency_conflict"));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Forbid();
            }
        }).RequireAuthorization("User").RequireRateLimiting("access-request")
            .Produces<AccessRequestResult>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests);
}
