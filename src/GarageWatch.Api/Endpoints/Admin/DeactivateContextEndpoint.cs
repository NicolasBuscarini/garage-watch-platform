using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Cameras;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class DeactivateContextEndpoint() : BaseEndpoint("Camera administration", "AdminDeactivateContext",
    "Deactivate a camera context", "Logically deactivates a context at the supplied version; contexts with enabled cameras conflict.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapDelete("/api/v1/admin/contexts/{contextId}", async (string contextId, long version,
            HttpContext context, ClaimsPrincipal principal, UserManager<ApplicationUser> users, ICameraCatalog catalog,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try
            {
                return Results.Ok(await catalog.DeactivateContextAsync(actor.Id, contextId, version, cancellationToken));
            }
            catch (Exception exception) when (exception is CatalogValidationException or CatalogConflictException or
                                              CatalogNotFoundException or UnauthorizedAccessException)
            {
                return EndpointSecurity.CatalogError(exception);
            }
        }).RequireAuthorization("Administrator")
            .Produces<CameraContextRecord>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
