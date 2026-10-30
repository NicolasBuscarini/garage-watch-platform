using GarageWatch.Api.Endpoints;
using GarageWatch.Infrastructure.Identity;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class ListAccessProfilesEndpoint() : BaseEndpoint("Access profiles", "AdminListAccessProfiles",
    "List access profiles", "Returns named profiles and their current immutable permission version; profiles never contain Administrator authority.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/access-rules", async (AccessProfileService profiles,
            CancellationToken cancellationToken) => Results.Ok(await profiles.ListAsync(cancellationToken)))
            .RequireAuthorization("Administrator")
            .Produces<IReadOnlyList<AccessProfileSummary>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
