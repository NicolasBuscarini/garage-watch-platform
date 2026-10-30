using GarageWatch.Application.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed record AccessRequestSummary(Guid Id, Guid UserId, string Username, string Purpose, string Status,
    DateTimeOffset CreatedAtUtc);

public sealed record AccessRequestPage(IReadOnlyList<AccessRequestSummary> Items, int Page, int PageSize,
    int TotalCount);

public sealed record AccessRequestResult(Guid Id, string Purpose, string Status, DateTimeOffset CreatedAtUtc);
public sealed class AccessRequestConflictException(string message) : Exception(message);

public sealed class AccessRequestService(IamDbContext database, IIamUnitOfWork unitOfWork,
    IIamAuditEventRepository auditEvents, TimeProvider timeProvider)
{
    public Task<AccessRequestResult> CreateAsync(Guid userId, Guid idempotencyKey, string purpose,
        CancellationToken cancellationToken) => unitOfWork.ExecuteAtomicallyAsync(async token =>
    {
        if (!AccessRequestPurposes.IsValid(purpose))
            throw new ArgumentException("Finalidade do pedido inválida.", nameof(purpose));
        if (idempotencyKey == Guid.Empty)
            throw new ArgumentException("A chave de idempotência é obrigatória.", nameof(idempotencyKey));

        var user = await database.Users.SingleOrDefaultAsync(item => item.Id == userId && item.IsActive, token)
            ?? throw new UnauthorizedAccessException("A conta não está ativa.");
        if (!await database.UserRoles.Join(database.Roles, userRole => userRole.RoleId, role => role.Id,
                (userRole, role) => new { userRole.UserId, role.Name })
            .AnyAsync(item => item.UserId == userId && item.Name == "User", token))
            throw new UnauthorizedAccessException("Somente contas User podem solicitar acesso.");

        var byKey = await database.AccessRequests.SingleOrDefaultAsync(item =>
            item.UserId == userId && item.IdempotencyKey == idempotencyKey, token);
        if (byKey is not null)
        {
            if (!string.Equals(byKey.Purpose, purpose, StringComparison.Ordinal))
                throw new AccessRequestConflictException("A chave foi usada com outra finalidade.");
            return Map(byKey);
        }

        var pending = await database.AccessRequests.OrderBy(item => item.CreatedAtUtc)
            .FirstOrDefaultAsync(item => item.UserId == userId && item.Status == "pending", token);
        if (pending is not null) return Map(pending);

        var request = new AccessRequest
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            IdempotencyKey = idempotencyKey,
            Purpose = purpose,
            Status = "pending",
            CreatedAtUtc = timeProvider.GetUtcNow()
        };
        database.AccessRequests.Add(request);
        auditEvents.Append(userId, userId, "access.request.created", request.Id.ToString("D"), request.CreatedAtUtc);
        return Map(request);
    }, cancellationToken);

    public async Task<AccessRequestResult?> GetPendingAsync(Guid userId, CancellationToken cancellationToken)
    {
        var request = await database.AccessRequests.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == "pending")
            .OrderByDescending(item => item.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        return request is null ? null : Map(request);
    }

    public async Task<AccessRequestPage> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1 || pageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(page), "Paginação fora dos limites.");
        var query = database.AccessRequests.AsNoTracking();
        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(item => item.CreatedAtUtc).ThenBy(item => item.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new AccessRequestSummary(item.Id, item.UserId, item.User!.UserName!, item.Purpose,
                item.Status, item.CreatedAtUtc))
            .ToArrayAsync(cancellationToken);
        return new AccessRequestPage(items, page, pageSize, totalCount);
    }

    public Task ReviewAsync(Guid administratorId, Guid requestId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var administrator = await database.Users.SingleOrDefaultAsync(item =>
                item.Id == administratorId && item.IsActive, token);
            if (administrator is null || !await database.UserRoles.Join(database.Roles,
                    userRole => userRole.RoleId, role => role.Id,
                    (userRole, role) => new { userRole.UserId, role.Name })
                .AnyAsync(item => item.UserId == administratorId && item.Name == "Administrator", token))
                throw new UnauthorizedAccessException("A operação requer administrador ativo.");

            var request = await database.AccessRequests.SingleOrDefaultAsync(item => item.Id == requestId, token)
                ?? throw new KeyNotFoundException("Pedido não encontrado.");
            if (request.Status == "reviewed") return;
            request.Status = "reviewed";
            auditEvents.Append(administratorId, request.UserId, "access.request.reviewed", request.Id.ToString("D"),
                timeProvider.GetUtcNow());
        }, cancellationToken);

    private static AccessRequestResult Map(AccessRequest request) =>
        new(request.Id, request.Purpose, request.Status, request.CreatedAtUtc);
}
