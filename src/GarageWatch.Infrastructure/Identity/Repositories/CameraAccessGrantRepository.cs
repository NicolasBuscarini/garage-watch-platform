using GarageWatch.Application.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity.Repositories;

public sealed class CameraAccessGrantRepository(IamDbContext database)
    : BaseRepository<CameraAccessGrant>(database), ICameraAccessGrantRepository
{
    public async Task<CameraAccessGrantRecord?> GetAsync(Guid id, bool withDetails = false,
        CancellationToken cancellationToken = default) =>
        Map(await GetEntityAsync(grant => grant.Id == id, withDetails, cancellationToken));

    public async Task<IReadOnlyList<CameraAccessGrantRecord>> GetAllAsync(bool withDetails = false,
        CancellationToken cancellationToken = default) =>
        (await GetAllEntitiesAsync(withDetails, cancellationToken)).Select(grant => Map(grant)!).ToArray();

    public async Task<RepositoryPage<CameraAccessGrantRecord>> ListPaginatedAsync(int pageNumber, int pageSize,
        bool withDetails = false, CancellationToken cancellationToken = default)
    {
        var result = await ListEntitiesPaginatedAsync(pageNumber, pageSize, withDetails, cancellationToken);
        return new(result.Items.Select(grant => Map(grant)!).ToArray(), pageNumber, pageSize, result.TotalCount);
    }

    public async Task<CameraAccessGrantRecord?> FindActiveAsync(Guid userId, string cameraId,
        bool withDetails = false, CancellationToken cancellationToken = default) =>
        Map(await GetEntityAsync(grant => grant.UserId == userId && grant.CameraId == cameraId &&
            grant.RevokedAtUtc == null, withDetails, cancellationToken));

    public void Add(Guid userId, string contextId, string cameraId, Guid grantedByUserId,
        DateTimeOffset grantedAtUtc) => AddEntity(new CameraAccessGrant
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ContextId = contextId,
            CameraId = cameraId,
            GrantedByUserId = grantedByUserId,
            GrantedAtUtc = grantedAtUtc
        });

    public async Task<bool> RevokeActiveAsync(Guid userId, string cameraId, DateTimeOffset revokedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var grant = await TrackedQuery().FirstOrDefaultAsync(item => item.UserId == userId &&
            item.CameraId == cameraId && item.RevokedAtUtc == null, cancellationToken);
        if (grant is null) return false;
        grant.RevokedAtUtc = revokedAtUtc;
        return true;
    }

    protected override IQueryable<CameraAccessGrant> ApplyDetails(IQueryable<CameraAccessGrant> query) =>
        query.Include(grant => grant.User);

    protected override IOrderedQueryable<CameraAccessGrant> OrderForPaging(IQueryable<CameraAccessGrant> query) =>
        query.OrderByDescending(grant => grant.GrantedAtUtc).ThenBy(grant => grant.Id);

    private static CameraAccessGrantRecord? Map(CameraAccessGrant? grant) => grant is null ? null :
        new(grant.Id, grant.UserId, grant.ContextId, grant.CameraId, grant.GrantedByUserId,
            grant.GrantedAtUtc, grant.RevokedAtUtc, grant.User is null ? null :
                new IdentityUserSummary(grant.User.Id, grant.User.UserName ?? string.Empty, grant.User.IsActive));
}
