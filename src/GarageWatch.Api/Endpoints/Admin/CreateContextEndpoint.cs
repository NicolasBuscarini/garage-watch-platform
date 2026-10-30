using GarageWatch.Api.Contracts;
using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Cameras;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class CreateContextEndpoint() : BaseEndpoint("Camera administration", "AdminCreateContext",
    "Create a camera context", "Creates a context with an immutable identifier. Requires Administrator and same-origin Origin; mutation is audited.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/admin/contexts", async (CreateCameraContextRequest request, HttpContext context,
            ClaimsPrincipal principal, UserManager<ApplicationUser> users, ICameraCatalog catalog,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try
            {
                var created = await catalog.CreateContextAsync(actor.Id, request.ContextId, request.Name, cancellationToken);
                return Results.Created($"/api/v1/admin/contexts/{created.ContextId}", created);
            }
            catch (Exception exception) when (exception is CatalogValidationException or CatalogConflictException or
                                              CatalogNotFoundException or UnauthorizedAccessException)
            {
                return EndpointSecurity.CatalogError(exception);
            }
        }).RequireAuthorization("Administrator")
            .Produces<CameraContextRecord>(StatusCodes.Status201Created)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
