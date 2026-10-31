using System.Security.Claims;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Clips;

public sealed class GetClipMediaEndpoint() : BaseEndpoint("Clips", "GetClipMedia", "Stream authorized clip media",
    "Validates current clips.read scope and stored SHA-256 before serving local media with byte-range support.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/clips/{clipId}/media", async (string clipId, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, ClipLibraryService library, CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var result = await library.GetAsync(user.Id, await users.IsInRoleAsync(user, "Administrator"), clipId,
                cancellationToken);
            if (result.Details is null || result.MediaPath is null) return Results.NotFound();
            if (!await ClipLibraryService.HasValidHashAsync(result.MediaPath, result.Details.MediaSha256!, cancellationToken))
                return Results.NotFound();
            return Results.File(result.MediaPath, "video/mp2t", enableRangeProcessing: true);
        }).RequireAuthorization().Produces<byte[]>(StatusCodes.Status200OK, "video/mp2t")
            .Produces<byte[]>(StatusCodes.Status206PartialContent, "video/mp2t")
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
}
