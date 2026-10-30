using GarageWatch.Api.Endpoints;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Authentication;

public sealed class SessionEndpoint() : BaseEndpoint("Authentication", "GetSession", "Check browser session state",
    "Returns only whether this request has an authenticated session; it does not reveal identity or role.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/auth/session", (ClaimsPrincipal user) =>
            Results.Ok(new SessionResponse(user.Identity?.IsAuthenticated == true)))
            .AllowAnonymous().Produces<SessionResponse>(StatusCodes.Status200OK);
}
