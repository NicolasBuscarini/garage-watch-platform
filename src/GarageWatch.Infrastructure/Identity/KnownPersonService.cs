using GarageWatch.Application.Identity;
using Microsoft.EntityFrameworkCore;
using GarageWatch.Infrastructure.Identity.Repositories;

namespace GarageWatch.Infrastructure.Identity;

public sealed record KnownPersonSummary(string PublicId, string DisplayName, string Purpose,
    DateTimeOffset AuthorizationRecordedAtUtc, DateTimeOffset CreatedAtUtc, long Version);
public sealed record KnownPersonPage(IReadOnlyList<KnownPersonSummary> Items, int Page, int PageSize, int Total);
public sealed record KnownPersonResult(string PublicId, string DisplayName, string Purpose,
    DateTimeOffset AuthorizationRecordedAtUtc, DateTimeOffset CreatedAtUtc, long Version);
public sealed class KnownPersonService(IamDbContext database, IIamUnitOfWork unitOfWork,
    IIamAuditEventRepository auditEvents, TimeProvider timeProvider, IPublicIdGenerator publicIds)
{
    public async Task<KnownPersonResult> CreateAsync(Guid actorId, string? displayName, string? purpose,
        bool authorizationConfirmed, CancellationToken cancellationToken)
    {
        if (!authorizationConfirmed)
            throw new ArgumentException("A autorização da pessoa deve ser confirmada antes do cadastro.");
        var name = displayName?.Trim();
        var intendedPurpose = purpose?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new ArgumentException("Nome deve ter entre 1 e 128 caracteres.");
        if (string.IsNullOrWhiteSpace(intendedPurpose) || intendedPurpose.Length > 256)
            throw new ArgumentException("Finalidade deve ter entre 1 e 256 caracteres.");

        var now = timeProvider.GetUtcNow();
        var created = await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            var person = new KnownPerson
            {
                Id = Guid.NewGuid(),
                PublicId = publicIds.Generate(PublicIdPrefixes.KnownPersonPrefix),
                DisplayName = name,
                Purpose = intendedPurpose,
                AuthorizationRecordedAtUtc = now,
                CreatedAtUtc = now,
                Version = 1
            };
            database.KnownPeople.Add(person);
            auditEvents.Append(actorId, null, "known_person.created", person.PublicId, now);
            return person;
        }, cancellationToken);
        return Map(created);
    }

    public async Task<KnownPersonPage> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        if (page < 1 || pageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(page), "Paginação fora dos limites.");
        var query = database.KnownPeople.AsNoTracking().Where(person => person.DeletedAtUtc == null);
        var total = await query.CountAsync(cancellationToken);
        var offset = ((long)page - 1) * pageSize;
        var items = offset >= total ? [] : await query.OrderBy(person => person.DisplayName)
            .ThenBy(person => person.Id).Skip((int)offset).Take(pageSize)
            .Select(person => new KnownPersonSummary(person.PublicId!, person.DisplayName!, person.Purpose!,
                person.AuthorizationRecordedAtUtc!.Value, person.CreatedAtUtc, person.Version))
            .ToArrayAsync(cancellationToken);
        return new(items, page, pageSize, total);
    }

    public Task RemoveAsync(Guid actorId, string publicId, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            if (!PublicIdFormat.IsValid(publicId, PublicIdPrefixes.KnownPersonPrefix))
                throw new KeyNotFoundException("Pessoa não encontrada.");
            var person = await database.KnownPeople.SingleOrDefaultAsync(item => item.PublicId == publicId, token)
                ?? throw new KeyNotFoundException("Pessoa não encontrada.");
            if (person.DeletedAtUtc is not null) return;

            var now = timeProvider.GetUtcNow();
            person.DisplayName = null;
            person.Purpose = null;
            person.AuthorizationRecordedAtUtc = null;
            person.DeletedAtUtc = now;
            person.Version++;
            auditEvents.Append(actorId, null, "known_person.removed", publicId, now);
        }, cancellationToken);

    private static KnownPersonResult Map(KnownPerson person) => new(person.PublicId!, person.DisplayName!,
        person.Purpose!, person.AuthorizationRecordedAtUtc!.Value, person.CreatedAtUtc, person.Version);
}
