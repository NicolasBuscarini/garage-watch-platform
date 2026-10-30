using GarageWatch.Api.Endpoints;
using GarageWatch.Api.Presence;
using GarageWatch.Api.Contracts;
using GarageWatch.Application.Cameras;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Clients;

public sealed class HeartbeatEndpoint() : BaseEndpoint("Clients", "UpdatePresence", "Renew browser presence lease",
    "Renews an ephemeral client lease. The count is bounded and represents active browser instances, not unique people.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut("/api/v1/clients/presence", async (ClientPresenceHeartbeat request, ClientPresenceRegistry registry,
            HttpContext context, ICameraResourceAccess access, CancellationToken cancellationToken) =>
        {
            if (request.ClientId == Guid.Empty) return Results.BadRequest(new ApiErrorResponse("invalid_client_id"));
            var userId = Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed) ? parsed : Guid.Empty;
            if (userId == Guid.Empty || !await access.HasAnyLiveAccessAsync(userId, cancellationToken))
                return Results.Forbid();
            if (!registry.Heartbeat(request.ClientId, out var activeClients))
                return Results.Json(new PresenceCapacityErrorResponse("client_capacity_reached", activeClients),
                    statusCode: StatusCodes.Status429TooManyRequests);
            return Results.Ok(new PresenceResponse(activeClients, ClientPresenceRegistry.LeaseDuration.TotalSeconds));
        }).RequireAuthorization().Produces<PresenceResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<PresenceCapacityErrorResponse>(StatusCodes.Status429TooManyRequests)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
