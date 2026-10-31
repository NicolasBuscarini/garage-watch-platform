using GarageWatch.Api.Contracts;
using GarageWatch.Api.Endpoints;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class CreateAccessProfileEndpoint() : BaseEndpoint("Access profiles", "AdminCreateAccessProfile",
    "Create an access profile", "Creates a reusable named profile with permissions selected only from the finite delegable catalog. Name and permission set are audited.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/admin/access-rules", async (CreateAccessProfileRequest request, HttpContext context,
            ClaimsPrincipal principal, UserManager<ApplicationUser> users, AccessProfileService profiles,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            if (request.PermissionKeys is null) return Results.BadRequest(new ApiErrorResponse("invalid_profile"));
            try
            {
                var created = await profiles.CreateAsync(actor.Id, request.Name, request.PermissionKeys, cancellationToken);
                return Results.Created($"/api/v1/admin/access-rules/{created.PublicId}", created);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                              KeyNotFoundException or UnauthorizedAccessException)
            {
                return EndpointSecurity.AdminIdentityError(exception);
            }
        }).RequireAuthorization("Administrator")
            .Produces<AccessProfileSummary>(StatusCodes.Status201Created)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
