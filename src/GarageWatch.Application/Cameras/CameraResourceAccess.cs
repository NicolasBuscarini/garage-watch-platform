namespace GarageWatch.Application.Cameras;

public interface ICameraResourceAccess
{
    Task<bool> CanReadLiveAsync(Guid userId, string cameraId, CancellationToken cancellationToken = default);
}
