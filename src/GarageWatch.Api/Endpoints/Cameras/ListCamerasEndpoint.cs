using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Cameras;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;

namespace GarageWatch.Api.Endpoints.Cameras;

public sealed class ListCamerasEndpoint() : BaseEndpoint("Cameras", "ListCameras", "List cameras visible to the session",
    "Administrators see all catalog entries. Other monitoring sessions see only active grants for enabled cameras and contexts.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/cameras", async (ClaimsPrincipal principal, UserManager<ApplicationUser> users,
            ICameraCatalog catalog, CaptureOptions options, CaptureWorker capture, CancellationToken cancellationToken) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null || !user.IsActive) return Results.Unauthorized();
            var isAdministrator = await users.IsInRoleAsync(user, "Administrator");
            var cameras = await catalog.ListVisibleCamerasAsync(user.Id, isAdministrator, cancellationToken);
            return Results.Ok(cameras.Select(camera => new CameraSummaryResponse(camera.CameraId, camera.ContextId,
                camera.Name, camera.IsEnabled && camera.CameraId == options.CameraId ? capture.Status.State : "stopped",
                camera.IsEnabled && camera.CameraId == options.CameraId ? capture.Status.StreamEpoch : null,
                $"/api/v1/live/{camera.CameraId}/index.m3u8")));
        }).RequireAuthorization("Viewer").Produces<IReadOnlyList<CameraSummaryResponse>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
