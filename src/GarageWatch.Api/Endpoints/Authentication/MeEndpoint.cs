using GarageWatch.Api.Endpoints;
using GarageWatch.Api.Security;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Authentication;

public sealed class MeEndpoint() : BaseEndpoint("Authentication", "GetCurrentUser", "Get current account identity",
    "Returns the authenticated username and primary legacy role. Does not return grants, credentials, or security tokens.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/auth/me", async (ClaimsPrincipal principal, UserManager<ApplicationUser> users,
            AccessProfileService profiles, CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var roles = await users.GetRolesAsync(user);
            var primaryRole = roles.Contains("Administrator", StringComparer.Ordinal) ? "Administrator"
                : roles.Contains("Viewer", StringComparer.Ordinal) ? "Viewer" : "User";
            var canMonitor = primaryRole == "Administrator" ||
                await profiles.IsAllowedAnyCameraAsync(user.Id, "live.video.view", cancellationToken);
            return Results.Ok(new AuthenticatedUser(user.UserName ?? "", primaryRole, canMonitor));
        }).RequireAuthorization().Produces<AuthenticatedUser>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);
}
