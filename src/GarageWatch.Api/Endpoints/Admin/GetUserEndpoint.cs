using GarageWatch.Api.Contracts;
using GarageWatch.Api.Endpoints;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class GetUserEndpoint() : BaseEndpoint("User administration", "AdminGetUser",
    "Get user account details", "Returns username, active state, roles, and camera grant history; excludes email and credentials.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/users/{userId}", async (string userId, UserManager<ApplicationUser> users,
            AccessProfileService accessProfiles, CancellationToken cancellationToken) =>
        {
            if (!PublicIdFormat.IsValid(userId, PublicIdPrefixes.UserPrefix))
                return Results.NotFound(new ApiErrorResponse("user_not_found"));
            var user = await users.Users.SingleOrDefaultAsync(item => item.PublicId == userId, cancellationToken);
            if (user is null) return Results.NotFound(new ApiErrorResponse("user_not_found"));
            var assignments = await accessProfiles.GetAssignmentsAsync(user.Id, cancellationToken);
            var isAdministrator = await users.IsInRoleAsync(user, "Administrator");
            return Results.Ok(new AdminUserDetailsResponse(user.PublicId!, user.UserName ?? string.Empty, user.IsActive,
                isAdministrator, assignments));
        }).RequireAuthorization("Administrator")
            .Produces<AdminUserDetailsResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
