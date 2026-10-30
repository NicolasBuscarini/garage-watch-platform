using GarageWatch.Application.Cameras;
using GarageWatch.Application.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed class CameraResourceAccessService(
    IamDbContext database,
    IIamUnitOfWork unitOfWork,
    IIamAuditEventRepository auditEvents,
    UserManager<ApplicationUser> users,
    TimeProvider timeProvider) : ICameraResourceAccess
{
    public async Task<bool> CanReadLiveAsync(Guid userId, string cameraId,
        CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive) return false;

        var roles = await users.GetRolesAsync(user);
        var isAdministrator = roles.Contains("Administrator", StringComparer.Ordinal);
        var isViewer = roles.Contains("Viewer", StringComparer.Ordinal);
        var catalogCamera = await database.CameraCatalog.AsNoTracking()
            .Where(camera => camera.CameraId == cameraId && camera.IsEnabled && camera.Context.IsEnabled)
            .Select(camera => new { camera.CameraId, camera.ContextId })
            .SingleOrDefaultAsync(cancellationToken);

        var allowed = catalogCamera is not null && (isAdministrator || (isViewer &&
            await database.CameraAccessGrants.AsNoTracking().AnyAsync(grant => grant.UserId == userId &&
                grant.CameraId == catalogCamera.CameraId && grant.ContextId == catalogCamera.ContextId &&
                grant.RevokedAtUtc == null, cancellationToken)));
        if (allowed) return true;

        await unitOfWork.ExecuteAtomicallyAsync(token =>
        {
            auditEvents.Append(userId, userId, "camera.access.denied", IsSafeIdentifier(cameraId) ? cameraId : null,
                timeProvider.GetUtcNow());
            return Task.CompletedTask;
        }, cancellationToken);
        return false;
    }

    private static bool IsSafeIdentifier(string value) => value.Length is > 0 and <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
