using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity.Repositories;

public abstract class BaseRepository<TEntity>(IamDbContext database) where TEntity : class
{
    protected IamDbContext Database { get; } = database;
    protected DbSet<TEntity> Set => Database.Set<TEntity>();

    protected IQueryable<TEntity> ReadQuery(bool withDetails = false)
    {
        var query = Set.AsNoTracking();
        return withDetails ? ApplyDetails(query) : query;
    }

    protected IQueryable<TEntity> TrackedQuery(bool withDetails = false)
    {
        var query = Set.AsQueryable();
        return withDetails ? ApplyDetails(query) : query;
    }

    protected virtual IQueryable<TEntity> ApplyDetails(IQueryable<TEntity> query) => query;

    protected abstract IOrderedQueryable<TEntity> OrderForPaging(IQueryable<TEntity> query);

    protected Task<TEntity?> GetEntityAsync(Expression<Func<TEntity, bool>> predicate, bool withDetails,
        CancellationToken cancellationToken) => ReadQuery(withDetails).FirstOrDefaultAsync(predicate, cancellationToken);

    protected Task<List<TEntity>> GetAllEntitiesAsync(bool withDetails, CancellationToken cancellationToken) =>
        ReadQuery(withDetails).ToListAsync(cancellationToken);

    protected async Task<(IReadOnlyList<TEntity> Items, int TotalCount)> ListEntitiesPaginatedAsync(
        int pageNumber, int pageSize, bool withDetails, CancellationToken cancellationToken)
    {
        ValidatePage(pageNumber, pageSize);
        var query = ReadQuery(withDetails);
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await OrderForPaging(query)
            .Skip(checked((pageNumber - 1) * pageSize))
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return (items, totalCount);
    }

    protected void AddEntity(TEntity entity) => Set.Add(entity);

    private static void ValidatePage(int pageNumber, int pageSize)
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber));
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be between 1 and 100.");
        _ = checked((pageNumber - 1) * pageSize);
    }
}
