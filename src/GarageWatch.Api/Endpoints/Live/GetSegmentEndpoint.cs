using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Cameras;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Live;

public sealed class GetSegmentEndpoint() : BaseEndpoint("Live video", "GetLiveSegment",
    "Get an authenticated HLS segment", "Checks the current camera grant before resolving a local segment and enabling byte-range responses; client paths are not accepted.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/live/{cameraId}/segments/{segmentId}", async (string cameraId, string segmentId,
            ClaimsPrincipal principal, UserManager<ApplicationUser> users, ICameraResourceAccess cameraAccess,
            CaptureOptions captureOptions, LocalSegmentSpool spool, CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            if (!await cameraAccess.CanReadLiveAsync(user.Id, cameraId, cancellationToken)) return Results.NotFound();
            if (!string.Equals(cameraId, captureOptions.CameraId, StringComparison.Ordinal) ||
                !LocalSegmentSpool.IsSafeSegmentId(segmentId)) return Results.NotFound();
            var item = spool.GetRecentSegments(captureOptions.MaximumSegments)
                .FirstOrDefault(x => x.Manifest.SegmentId == segmentId);
            return item.Manifest is null ? Results.NotFound()
                : Results.File(item.Path, "video/mp2t", enableRangeProcessing: true);
        }).RequireAuthorization("Viewer")
            .Produces<byte[]>(StatusCodes.Status200OK, "video/mp2t")
            .Produces<byte[]>(StatusCodes.Status206PartialContent, "video/mp2t")
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
}
