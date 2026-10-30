using GarageWatch.Api.Contracts;
using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Cameras;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class UpdateContextEndpoint() : BaseEndpoint("Camera administration", "AdminUpdateContext",
    "Update a camera context", "Updates name/enabled state using optimistic version. Requires Administrator and same-origin Origin.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut("/api/v1/admin/contexts/{contextId}", async (string contextId, UpdateCameraContextRequest request,
            HttpContext context, ClaimsPrincipal principal, UserManager<ApplicationUser> users, ICameraCatalog catalog,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try
            {
                return Results.Ok(await catalog.UpdateContextAsync(actor.Id, contextId, request.Version, request.Name,
                    request.IsEnabled, cancellationToken));
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
