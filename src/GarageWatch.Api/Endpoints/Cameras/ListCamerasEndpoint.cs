using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Cameras;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Cameras;

public sealed class ListCamerasEndpoint() : BaseEndpoint("Cameras", "ListCameras", "List cameras visible to the session",
    "Administrators see all catalog entries. Other monitoring sessions see only active grants for enabled cameras and contexts.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/cameras", async (ClaimsPrincipal principal, UserManager<ApplicationUser> users,
            ICameraCatalog catalog, IamDbContext database, CaptureOptions options, CaptureWorker capture, CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var isAdministrator = await users.IsInRoleAsync(user, "Administrator");
            var cameras = await catalog.ListVisibleCamerasAsync(user.Id, isAdministrator, cancellationToken);
            var internalCameraIds = await database.CameraCatalog.AsNoTracking()
                .Where(item => cameras.Select(camera => camera.CameraId).Contains(item.PublicId!))
                .Select(item => new { item.PublicId, item.CameraId }).ToDictionaryAsync(item => item.PublicId!, item => item.CameraId,
                    cancellationToken);
            return Results.Ok(cameras.Select(camera => new CameraSummaryResponse(camera.CameraId, camera.ContextId,
                camera.Name, camera.IsEnabled && internalCameraIds.GetValueOrDefault(camera.CameraId) == options.CameraId ? capture.Status.State : "stopped",
                camera.IsEnabled && internalCameraIds.GetValueOrDefault(camera.CameraId) == options.CameraId ? capture.Status.StreamEpoch : null,
                $"/api/v1/live/{camera.CameraId}/index.m3u8")));
        }).RequireAuthorization("Viewer").Produces<IReadOnlyList<CameraSummaryResponse>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
