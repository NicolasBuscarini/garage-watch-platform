using System.Security.Claims;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Clips;

public sealed class GetClipThumbnailEndpoint() : BaseEndpoint("Clips", "GetClipThumbnail", "Get authorized clip thumbnail",
    "Checks current camera scope and thumbnail SHA-256 before serving JPEG content.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/clips/{clipId}/thumbnail", async (string clipId, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, ClipLibraryService library, CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var result = await library.GetAsync(user.Id, await users.IsInRoleAsync(user, "Administrator"), clipId,
                cancellationToken);
            if (result.Details is null || result.ThumbnailPath is null ||
                !await ClipLibraryService.HasValidHashAsync(result.ThumbnailPath, result.Details.ThumbnailSha256!, cancellationToken))
                return Results.NotFound();
            return Results.File(result.ThumbnailPath, "image/jpeg");
        }).RequireAuthorization().Produces<byte[]>(StatusCodes.Status200OK, "image/jpeg")
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
}
