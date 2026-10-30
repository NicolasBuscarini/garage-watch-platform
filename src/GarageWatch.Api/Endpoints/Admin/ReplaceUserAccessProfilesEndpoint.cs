using GarageWatch.Api.Contracts;
using GarageWatch.Api.Endpoints;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class ReplaceUserAccessProfilesEndpoint() : BaseEndpoint("User administration", "AdminReplaceUserAccessProfiles",
    "Assign access profiles to a user", "Replaces profile assignments and their explicit camera/context scopes atomically. Permissions are not editable on a user; Administrator is never assignable.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut("/api/v1/admin/users/{userId:guid}/access-rules", async (Guid userId,
            SetUserAccessProfilesRequest request, HttpContext context, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, AccessProfileService profiles, CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            if (request.Assignments is null) return Results.BadRequest(new ApiErrorResponse("invalid_assignments"));
            try
            {
                await profiles.ReplaceAssignmentsAsync(actor.Id, userId, request.Assignments, cancellationToken);
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
