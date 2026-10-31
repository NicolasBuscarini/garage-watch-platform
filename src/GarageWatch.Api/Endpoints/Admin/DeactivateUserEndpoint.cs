using GarageWatch.Api.Endpoints;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class DeactivateUserEndpoint() : BaseEndpoint("User administration", "AdminDeactivateUser",
    "Deactivate a user account", "Soft-disables the account, revokes camera grants in the same transaction, and invalidates sessions; hard deletion is not performed.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/api/v1/admin/users/{userId}", async (string userId, HttpContext context,
            ClaimsPrincipal principal, UserManager<ApplicationUser> users, IdentityAdministrationService administration,
            IamDbContext database, CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            if (!PublicIdFormat.IsValid(userId, PublicIdPrefixes.UserPrefix))
                return Results.NotFound(new ApiErrorResponse("user_not_found"));
            var targetId = await database.Users.Where(item => item.PublicId == userId)
                .Select(item => (Guid?)item.Id).SingleOrDefaultAsync(cancellationToken);
            if (targetId is null) return Results.NotFound(new ApiErrorResponse("user_not_found"));
            try
            {
                await administration.DeactivateUserAsync(actor.Id, targetId.Value, cancellationToken);
                return Results.NoContent();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                              KeyNotFoundException or UnauthorizedAccessException)
            {
                return EndpointSecurity.AdminIdentityError(exception);
            }
        }).RequireAuthorization("Administrator")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
