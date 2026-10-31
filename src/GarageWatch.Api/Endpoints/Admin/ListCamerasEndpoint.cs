using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Cameras;
using GarageWatch.Infrastructure.Identity;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class ListCamerasEndpoint() : BaseEndpoint("Camera administration", "AdminListCameras",
    "List catalog cameras", "Returns a bounded page and optional context filter; Administrator access is required.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/cameras", async (int? page, int? pageSize, string? contextId,
            ICameraCatalog catalog, CancellationToken cancellationToken) =>
        {
            if (contextId is not null && !PublicIdFormat.IsValid(contextId, PublicIdPrefixes.ContextPrefix))
                return Results.BadRequest(new ApiErrorResponse("invalid_context_id"));
            return Results.Ok(await catalog.ListCamerasAsync(page ?? 1, pageSize ?? 50, contextId, cancellationToken));
        })
            .RequireAuthorization("Administrator")
            .Produces<CatalogPage<CameraCatalogRecord>>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
