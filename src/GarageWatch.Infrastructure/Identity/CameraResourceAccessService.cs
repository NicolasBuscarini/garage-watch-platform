using GarageWatch.Application.Cameras;
using GarageWatch.Application.Authorization;
using GarageWatch.Application.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed class CameraResourceAccessService(
    IamDbContext database,
    IIamUnitOfWork unitOfWork,
    IIamAuditEventRepository auditEvents,
    AccessProfileService profiles,
    TimeProvider timeProvider) : ICameraResourceAccess
{
    public async Task<bool> CanReadLiveAsync(Guid userId, string cameraId,
        CancellationToken cancellationToken = default)
    {
        var catalogCamera = await database.CameraCatalog.AsNoTracking()
            .Where(camera => camera.PublicId == cameraId && camera.IsEnabled && camera.Context.IsEnabled)
            .Select(camera => new { camera.CameraId, camera.ContextId })
            .SingleOrDefaultAsync(cancellationToken);

        var allowed = catalogCamera is not null && await profiles.IsAllowedAsync(userId,
            new PermissionRequest("live.video.view", catalogCamera.ContextId, catalogCamera.CameraId), cancellationToken);
        if (allowed) return true;

        await unitOfWork.ExecuteAtomicallyAsync(token =>
        {
            auditEvents.Append(userId, userId, "camera.access.denied", PublicIdFormat.IsValid(cameraId,
                    PublicIdPrefixes.CameraPrefix) ? cameraId : null,
                timeProvider.GetUtcNow());
            return Task.CompletedTask;
        }, cancellationToken);
        return false;
    }

    public async Task<bool> HasAnyLiveAccessAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var cameras = await database.CameraCatalog.AsNoTracking()
            .Where(camera => camera.IsEnabled && camera.Context.IsEnabled)
            .Select(camera => new { camera.ContextId, camera.CameraId }).ToArrayAsync(cancellationToken);
        foreach (var camera in cameras)
            if (await profiles.IsAllowedAsync(userId,
                    new PermissionRequest("live.video.view", camera.ContextId, camera.CameraId), cancellationToken))
                return true;
        return false;
    }

    private static bool IsSafeIdentifier(string value) => value.Length is > 0 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
