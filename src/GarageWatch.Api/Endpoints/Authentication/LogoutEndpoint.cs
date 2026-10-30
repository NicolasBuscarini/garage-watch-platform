using GarageWatch.Api.Endpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Authentication;

public sealed class LogoutEndpoint() : BaseEndpoint("Authentication", "Logout", "End the current session",
    "Clears the authenticated application cookie for this browser session.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Results.NoContent();
        }).RequireAuthorization().Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized);
}
