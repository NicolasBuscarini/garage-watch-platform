namespace GarageWatch.Application.Identity;

public sealed record RepositoryPage<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, int TotalCount);

public sealed record IdentityUserSummary(Guid Id, string UserName, bool IsActive);

public sealed record CameraAccessGrantRecord(Guid Id, Guid UserId, string ContextId, string CameraId,
    Guid GrantedByUserId, DateTimeOffset GrantedAtUtc, DateTimeOffset? RevokedAtUtc,
    IdentityUserSummary? User);

public sealed record IamAuditEventRecord(Guid Id, Guid? ActorUserId, Guid? SubjectUserId, string Action,
    string? ResourceId, DateTimeOffset OccurredAtUtc, IdentityUserSummary? Actor, IdentityUserSummary? Subject);

public interface ICameraAccessGrantRepository
{
    Task<CameraAccessGrantRecord?> GetAsync(Guid id, bool withDetails = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CameraAccessGrantRecord>> GetAllAsync(bool withDetails = false,
        CancellationToken cancellationToken = default);

    Task<RepositoryPage<CameraAccessGrantRecord>> ListPaginatedAsync(int pageNumber, int pageSize,
        bool withDetails = false, CancellationToken cancellationToken = default);

    Task<CameraAccessGrantRecord?> FindActiveAsync(Guid userId, string cameraId, bool withDetails = false,
        CancellationToken cancellationToken = default);

    void Add(Guid userId, string contextId, string cameraId, Guid grantedByUserId, DateTimeOffset grantedAtUtc);

    Task<bool> RevokeActiveAsync(Guid userId, string cameraId, DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default);
}

public interface IIamAuditEventRepository
{
    Task<IamAuditEventRecord?> GetAsync(Guid id, bool withDetails = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IamAuditEventRecord>> GetAllAsync(bool withDetails = false,
        CancellationToken cancellationToken = default);

    Task<RepositoryPage<IamAuditEventRecord>> ListPaginatedAsync(int pageNumber, int pageSize,
        bool withDetails = false, CancellationToken cancellationToken = default);

    void Append(Guid? actorUserId, Guid? subjectUserId, string action, string? resourceId,
        DateTimeOffset occurredAtUtc);
}

public interface IIamUnitOfWork
{
    Task<TResult> ExecuteAtomicallyAsync<TResult>(Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default);

    Task ExecuteAtomicallyAsync(Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);
}
