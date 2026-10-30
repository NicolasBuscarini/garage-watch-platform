using GarageWatch.Application.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity.Repositories;

public sealed class IamAuditEventRepository(IamDbContext database)
    : BaseRepository<IamAuditEvent>(database), IIamAuditEventRepository
{
    public async Task<IamAuditEventRecord?> GetAsync(Guid id, bool withDetails = false,
        CancellationToken cancellationToken = default) =>
        Map(await GetEntityAsync(entry => entry.Id == id, withDetails, cancellationToken));

    public async Task<IReadOnlyList<IamAuditEventRecord>> GetAllAsync(bool withDetails = false,
        CancellationToken cancellationToken = default) =>
        (await GetAllEntitiesAsync(withDetails, cancellationToken)).Select(entry => Map(entry)!).ToArray();

    public async Task<RepositoryPage<IamAuditEventRecord>> ListPaginatedAsync(int pageNumber, int pageSize,
        bool withDetails = false, CancellationToken cancellationToken = default)
    {
        var result = await ListEntitiesPaginatedAsync(pageNumber, pageSize, withDetails, cancellationToken);
        return new(result.Items.Select(entry => Map(entry)!).ToArray(), pageNumber, pageSize, result.TotalCount);
    }

    public void Append(Guid? actorUserId, Guid? subjectUserId, string action, string? resourceId,
        DateTimeOffset occurredAtUtc) => AddEntity(new IamAuditEvent
        {
            Id = Guid.NewGuid(),
            ActorUserId = actorUserId,
            SubjectUserId = subjectUserId,
            Action = action,
            ResourceId = resourceId,
            OccurredAtUtc = occurredAtUtc
        });

    protected override IQueryable<IamAuditEvent> ApplyDetails(IQueryable<IamAuditEvent> query) =>
        query.Include(entry => entry.Actor).Include(entry => entry.Subject);

    protected override IOrderedQueryable<IamAuditEvent> OrderForPaging(IQueryable<IamAuditEvent> query) =>
        query.OrderByDescending(entry => entry.OccurredAtUtc).ThenBy(entry => entry.Id);

    private static IamAuditEventRecord? Map(IamAuditEvent? entry) => entry is null ? null :
        new(entry.Id, entry.ActorUserId, entry.SubjectUserId, entry.Action, entry.ResourceId,
            entry.OccurredAtUtc, Map(entry.Actor), Map(entry.Subject));

    private static IdentityUserSummary? Map(ApplicationUser? user) => user is null ? null :
        new(user.Id, user.UserName ?? string.Empty, user.IsActive);
}
