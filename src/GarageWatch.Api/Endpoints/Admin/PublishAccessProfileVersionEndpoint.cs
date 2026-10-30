using GarageWatch.Api.Contracts;
using GarageWatch.Api.Endpoints;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class PublishAccessProfileVersionEndpoint() : BaseEndpoint("Access profiles", "AdminPublishAccessProfileVersion",
    "Publish a profile version", "Publishes an immutable next version with optimistic concurrency. Existing user assignments remain pinned until explicitly reassigned.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/admin/access-rules/{profileId:guid}/versions", async (Guid profileId,
            PublishAccessProfileVersionRequest request, HttpContext context, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, AccessProfileService profiles, CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            if (request.PermissionKeys is null) return Results.BadRequest(new ApiErrorResponse("invalid_profile"));
            try
            {
                return Results.Ok(await profiles.PublishVersionAsync(actor.Id, profileId, request.ExpectedVersion,
                    request.PermissionKeys, cancellationToken));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                              KeyNotFoundException or UnauthorizedAccessException)
            {
                return EndpointSecurity.AdminIdentityError(exception);
            }
        }).RequireAuthorization("Administrator")
            .Produces<AccessProfileSummary>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
