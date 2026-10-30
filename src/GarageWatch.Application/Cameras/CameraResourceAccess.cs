namespace GarageWatch.Application.Cameras;

public interface ICameraResourceAccess
{
    Task<bool> CanReadLiveAsync(Guid userId, string cameraId, CancellationToken cancellationToken = default);
    Task<bool> HasAnyLiveAccessAsync(Guid userId, CancellationToken cancellationToken = default);
}
