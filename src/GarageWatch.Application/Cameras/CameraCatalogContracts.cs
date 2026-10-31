namespace GarageWatch.Application.Cameras;

public sealed record CatalogPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record CameraContextRecord(
    string ContextId,
    string Name,
    bool IsEnabled,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record CameraCatalogRecord(
    string CameraId,
    string ContextId,
    string Name,
    bool IsEnabled,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public interface ICameraCatalog
{
    Task<CatalogPage<CameraContextRecord>> ListContextsAsync(int page, int pageSize,
        CancellationToken cancellationToken = default);

    Task<CatalogPage<CameraCatalogRecord>> ListCamerasAsync(int page, int pageSize, string? contextId = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CameraCatalogRecord>> ListVisibleCamerasAsync(Guid userId, bool isAdministrator,
        CancellationToken cancellationToken = default);

    Task<CameraContextRecord> CreateContextAsync(Guid actorUserId, string name,
        CancellationToken cancellationToken = default);

    Task<CameraContextRecord> UpdateContextAsync(Guid actorUserId, string contextId, long expectedVersion,
        string name, bool isEnabled, CancellationToken cancellationToken = default);

    Task<CameraContextRecord> DeactivateContextAsync(Guid actorUserId, string contextId, long expectedVersion,
        CancellationToken cancellationToken = default);

    Task<CameraCatalogRecord> CreateCameraAsync(Guid actorUserId, string contextId, string name,
        CancellationToken cancellationToken = default);

    Task<CameraCatalogRecord> UpdateCameraAsync(Guid actorUserId, string cameraId, long expectedVersion,
        string contextId, string name, bool isEnabled, CancellationToken cancellationToken = default);

    Task<CameraCatalogRecord> DeactivateCameraAsync(Guid actorUserId, string cameraId, long expectedVersion,
        CancellationToken cancellationToken = default);
}

public sealed class CatalogNotFoundException(string message) : Exception(message);

public sealed class CatalogConflictException(string message) : Exception(message);

public sealed class CatalogValidationException(string message) : Exception(message);
