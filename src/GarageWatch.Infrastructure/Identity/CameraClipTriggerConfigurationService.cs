using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using GarageWatch.Application.Identity;

namespace GarageWatch.Infrastructure.Identity;

public sealed record CameraClipTriggerConfiguration(string CameraId, bool Enabled, long Version,
    DateTimeOffset UpdatedAtUtc);

public sealed class CameraClipTriggerConfigurationService(IamDbContext database, IIamUnitOfWork unitOfWork,
    UserManager<ApplicationUser> users, TimeProvider timeProvider)
{
    public async Task<CameraClipTriggerConfiguration> GetAsync(string publicCameraId,
        CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(publicCameraId, PublicIdPrefixes.CameraPrefix)) throw new KeyNotFoundException();
        return await database.CameraCatalog.AsNoTracking().Where(camera => camera.PublicId == publicCameraId)
            .Select(camera => new CameraClipTriggerConfiguration(camera.PublicId!, camera.ClipTriggerEnabled,
                camera.ClipTriggerVersion, camera.UpdatedAtUtc)).SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException();
    }

    public async Task<CameraClipTriggerConfiguration> UpdateAsync(Guid actorId, string publicCameraId,
        long expectedVersion, bool enabled, CancellationToken cancellationToken)
    {
        if (expectedVersion < 0 || !PublicIdFormat.IsValid(publicCameraId, PublicIdPrefixes.CameraPrefix))
            throw new ArgumentException("Configuração de gatilho inválida.");
        return await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var actor = await users.FindByIdAsync(actorId.ToString());
            if (actor is null || !actor.IsActive || !await users.IsInRoleAsync(actor, "Administrator"))
                throw new UnauthorizedAccessException();
            var camera = await database.CameraCatalog.SingleOrDefaultAsync(item => item.PublicId == publicCameraId, token)
                ?? throw new KeyNotFoundException();
            if (camera.ClipTriggerVersion != expectedVersion)
                throw new ClipTriggerConfigurationConflictException();
            if (camera.ClipTriggerEnabled != enabled)
            {
                camera.ClipTriggerEnabled = enabled;
                camera.ClipTriggerVersion = checked(camera.ClipTriggerVersion + 1);
                camera.UpdatedAtUtc = timeProvider.GetUtcNow();
                database.IamAuditEvents.Add(new IamAuditEvent
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = actorId,
                    SubjectUserId = actorId,
                    Action = "clip.trigger.configuration.updated",
                    ResourceId = publicCameraId,
                    OccurredAtUtc = camera.UpdatedAtUtc
                });
            }
            return new CameraClipTriggerConfiguration(camera.PublicId!, camera.ClipTriggerEnabled,
                camera.ClipTriggerVersion, camera.UpdatedAtUtc);
        }, cancellationToken);
    }

    public async Task<bool> IsEnabledAsync(string internalCameraId, CancellationToken cancellationToken) =>
        await database.CameraCatalog.AsNoTracking().AnyAsync(camera => camera.CameraId == internalCameraId &&
            camera.IsEnabled && camera.Context.IsEnabled && camera.ClipTriggerEnabled, cancellationToken);
}

public sealed class ClipTriggerConfigurationConflictException : Exception;
