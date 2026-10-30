using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Authorization;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class ListPermissionCatalogEndpoint() : BaseEndpoint("Access profiles", "AdminListPermissionCatalog",
    "List assignable permissions", "Returns only the application's finite catalog. Only explicitly delegable keys can be selected for a profile.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/permission-catalog", () =>
            Results.Ok(PermissionCatalog.All.Where(item => item.IsDelegable)))
            .RequireAuthorization("Administrator")
            .Produces<IReadOnlyCollection<PermissionDefinition>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
