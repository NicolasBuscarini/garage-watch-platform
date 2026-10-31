using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Clips;

public sealed class GetClipEndpoint() : BaseEndpoint("Clips", "GetClip", "Get authorized clip details",
    "Resolves metadata and media references only after the current clips.read camera grant is checked.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/clips/{publicId}", async (string publicId, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, ClipLibraryService library, CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var admin = await users.IsInRoleAsync(user, "Administrator");
            var result = await library.GetAsync(user.Id, admin, publicId, cancellationToken);
            if (result.Details is null) return Results.NotFound();
            var detail = result.Details;
            var item = detail.Clip;
            return Results.Ok(new ClipDetailsResponse(new ClipListItemResponse(item.PublicId, item.CameraId,
                item.StartedAtUtc, item.EndedAtUtc, item.State, item.ArchiveStatus, item.AnalysisStatus,
                item.ThumbnailUrl, item.SizeBytes, item.TriggerReason, item.TriggerConfigVersion), detail.MediaUrl,
                detail.MediaSha256, detail.ThumbnailSha256, detail.AttemptCount, detail.UpdatedAtUtc));
        }).RequireAuthorization().Produces<ClipDetailsResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
}
