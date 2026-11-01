using System.Security.Claims;
using GarageWatch.Api.Errors;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Clips;

public sealed class GetClipMediaEndpoint() : BaseEndpoint("Clips", "GetClipMedia", "Stream authorized clip media",
    "Validates the current clips.read scope and the stored SHA-256, then serves a derived video-only MP4 copy with byte-range support. Browsers cannot demux the stored MPEG-TS clip.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/clips/{publicId}/media", async (string publicId, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, ClipLibraryService library, ClipPlaybackCache playback,
            CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var result = await library.GetAsync(user.Id, await users.IsInRoleAsync(user, "Administrator"), publicId,
                cancellationToken);
            if (result.Details is null || result.MediaPath is null) return Results.NotFound();
            if (!await ClipLibraryService.HasValidHashAsync(result.MediaPath, result.Details.MediaSha256!,
                    cancellationToken))
                return Results.NotFound();
            var playbackPath = await playback.EnsureAsync(Path.GetFileNameWithoutExtension(result.MediaPath),
                result.MediaPath, result.Details.MediaSha256!, cancellationToken);
            if (playbackPath is null)
                return Results.Json(new ApiErrorResponse("clip_playback_unavailable"),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            return Results.File(playbackPath, "video/mp4", enableRangeProcessing: true);
        }).RequireAuthorization().Produces<byte[]>(StatusCodes.Status200OK, "video/mp4")
            .Produces<byte[]>(StatusCodes.Status206PartialContent, "video/mp4")
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
}
