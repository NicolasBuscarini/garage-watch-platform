using GarageWatch.Application.Authorization;
using GarageWatch.Application.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed record AccessProfileSummary(string PublicId, string CurrentVersionPublicId, string Name, bool IsActive, long Version,
    DateTimeOffset UpdatedAtUtc, IReadOnlyList<string> PermissionKeys);

public sealed record AccessProfileAssignmentSummary(string AssignmentPublicId, string ProfilePublicId, string ProfileVersionPublicId, string ProfileName,
    long ProfileVersion, IReadOnlyList<PermissionScope> Scopes, DateTimeOffset AssignedAtUtc);

public sealed class AccessProfileService(
    IamDbContext database,
    IIamUnitOfWork unitOfWork,
    IIamAuditEventRepository audit,
    UserManager<ApplicationUser> users,
    IPermissionEvaluator evaluator,
    IPublicIdGenerator publicIds,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<AccessProfileSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var profiles = await database.AccessProfiles.AsNoTracking()
            .Include(profile => profile.Versions).ThenInclude(version => version.Permissions)
            .OrderBy(profile => profile.Name).ToArrayAsync(cancellationToken);
        return profiles.Select(profile =>
        {
            var current = profile.Versions.Single(version => version.Version == profile.Version);
            return new AccessProfileSummary(profile.PublicId!, current.PublicId!, profile.Name, profile.IsActive, profile.Version,
                profile.UpdatedAtUtc, current.Permissions.Select(permission => permission.PermissionKey)
                    .Order(StringComparer.Ordinal).ToArray());
        }).ToArray();
    }

    public Task<AccessProfileSummary> CreateAsync(Guid actorId, string name, IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken) => unitOfWork.ExecuteAtomicallyAsync(async token =>
    {
        await EnsureAdmin(actorId);
        var normalized = ValidateName(name);
        var permissions = ValidatePermissions(keys);
        if (await database.AccessProfiles.AnyAsync(profile => profile.Name == normalized, token))
            throw new InvalidOperationException("Já existe um perfil com esse nome.");
        var now = timeProvider.GetUtcNow();
        var profile = new AccessProfile
        {
            Id = Guid.NewGuid(),
            PublicId = publicIds.Generate(PublicIdPrefixes.AccessProfilePrefix),
            Name = normalized,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        var version = NewVersion(profile.Id, 1, actorId, now, permissions, publicIds);
        database.AccessProfiles.Add(profile);
        database.AccessProfileVersions.Add(version);
        audit.Append(actorId, actorId, "access_profile.created", profile.Id.ToString("D"), now);
        return Map(profile, version, permissions);
    }, cancellationToken);

    public Task<AccessProfileSummary> PublishVersionAsync(Guid actorId, Guid profileId, long expectedVersion,
        IReadOnlyCollection<string> keys, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            await EnsureAdmin(actorId);
            if (expectedVersion < 1) throw new ArgumentException("Versão inválida.");
            var permissions = ValidatePermissions(keys);
            var profile = await database.AccessProfiles.SingleOrDefaultAsync(item => item.Id == profileId, token)
                ?? throw new KeyNotFoundException("Perfil não encontrado.");
            if (!profile.IsActive) throw new InvalidOperationException("Perfil desativado.");
            if (profile.Version != expectedVersion) throw new InvalidOperationException("A versão do perfil mudou.");
            var next = checked(profile.Version + 1);
            var now = timeProvider.GetUtcNow();
            var published = NewVersion(profile.Id, next, actorId, now, permissions, publicIds);
            database.AccessProfileVersions.Add(published);
            profile.Version = next;
            profile.UpdatedAtUtc = now;
            audit.Append(actorId, actorId, "access_profile.version_published", profile.Id.ToString("D"), now);
            return Map(profile, published, permissions);
        }, cancellationToken);

    public Task ReplaceAssignmentsAsync(Guid actorId, Guid userId,
        IReadOnlyCollection<ProfileAssignmentRequest> requested, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            await EnsureAdmin(actorId);
            var user = await users.FindByIdAsync(userId.ToString())
                ?? throw new KeyNotFoundException("Conta não encontrada.");
            if (!user.IsActive) throw new InvalidOperationException("Conta desativada.");
            if (await users.IsInRoleAsync(user, "Administrator"))
                throw new InvalidOperationException("Administrator não recebe perfis delegáveis.");

            var normalized = ValidateAssignments(requested);
            var versionIds = normalized.Select(item => item.ProfileVersionId).Distinct().ToArray();
            var versions = await database.AccessProfileVersions.AsNoTracking()
                .Where(version => versionIds.Contains(version.Id) && version.Profile!.IsActive)
                .Select(version => new { version.Id, version.ProfileId, version.Version })
                .ToDictionaryAsync(version => version.Id, token);
            if (versions.Count != versionIds.Length ||
                versions.Values.Select(version => version.ProfileId).Distinct().Count() != versionIds.Length)
                throw new ArgumentException("Um ou mais perfis não estão disponíveis.");

            var catalogPairs = await database.CameraCatalog.AsNoTracking()
                .Where(camera => camera.IsEnabled && camera.Context.IsEnabled)
                .Select(camera => new { camera.ContextId, camera.CameraId }).ToArrayAsync(token);
            var enabledPairs = catalogPairs.Select(item => new PermissionScope(item.ContextId, item.CameraId)).ToHashSet();
            if (normalized.SelectMany(item => item.Scopes).Any(scope => !enabledPairs.Contains(scope)))
                throw new ArgumentException("O escopo contém câmera/contexto inexistente ou desativado.");

            var current = await database.UserAccessProfileAssignments
                .Where(item => item.UserId == userId && item.RevokedAtUtc == null).ToListAsync(token);
            var now = timeProvider.GetUtcNow();
            foreach (var assignment in current) assignment.RevokedAtUtc = now;
            foreach (var item in normalized)
            {
                var assignment = new UserAccessProfileAssignment
                {
                    Id = Guid.NewGuid(),
                    PublicId = publicIds.Generate(PublicIdPrefixes.ProfileAssignmentPrefix),
                    UserId = userId,
                    AccessProfileVersionId = item.ProfileVersionId,
                    AssignedByUserId = actorId,
                    AssignedAtUtc = now
                };
                foreach (var scope in item.Scopes.Distinct())
                    assignment.Scopes.Add(new UserAccessProfileScope
                    {
                        AssignmentId = assignment.Id,
                        ContextId = scope.ContextId,
                        CameraId = scope.CameraId
                    });
                database.UserAccessProfileAssignments.Add(assignment);
            }
            var stamp = await users.UpdateSecurityStampAsync(user);
            if (!stamp.Succeeded) throw new InvalidOperationException("Não foi possível revogar sessões existentes.");
            audit.Append(actorId, userId, "access_profiles.assigned", null, now);
        }, cancellationToken);

    public async Task<IReadOnlyList<AccessProfileAssignmentSummary>> GetAssignmentsAsync(Guid userId,
        CancellationToken cancellationToken)
    {
        var assignments = await database.UserAccessProfileAssignments.AsNoTracking()
            .Include(item => item.ProfileVersion).ThenInclude(version => version!.Profile)
            .Include(item => item.Scopes)
            .Where(item => item.UserId == userId && item.RevokedAtUtc == null && item.ProfileVersion!.Profile!.IsActive)
            .OrderBy(item => item.ProfileVersion!.Profile!.Name).ToArrayAsync(cancellationToken);
        var cameras = await database.CameraCatalog.AsNoTracking().Select(camera => new
        {
            camera.ContextId,
            camera.CameraId,
            camera.PublicId,
            ContextPublicId = camera.Context.PublicId
        }).ToDictionaryAsync(camera => (camera.ContextId, camera.CameraId), cancellationToken);
        return assignments.Select(item => new AccessProfileAssignmentSummary(item.PublicId!,
            item.ProfileVersion!.Profile!.PublicId!, item.ProfileVersion.PublicId!, item.ProfileVersion.Profile.Name,
            item.ProfileVersion.Version, item.Scopes.Select(scope =>
            {
                var camera = cameras[(scope.ContextId, scope.CameraId)];
                return new PermissionScope(camera.ContextPublicId!, camera.PublicId!);
            }).ToArray(), item.AssignedAtUtc)).ToArray();
    }

    public async Task<AuthorizationSubject> GetSubjectAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive) return new(false, new HashSet<string>(), []);
        var userRoles = await users.GetRolesAsync(user);
        var isAdmin = userRoles.Contains("Administrator", StringComparer.Ordinal);
        var assignments = await database.UserAccessProfileAssignments.AsNoTracking()
            .Include(item => item.ProfileVersion).ThenInclude(version => version!.Profile)
            .Include(item => item.ProfileVersion).ThenInclude(version => version!.Permissions)
            .Include(item => item.Scopes)
            .Where(item => item.UserId == userId && item.RevokedAtUtc == null && item.ProfileVersion!.Profile!.IsActive)
            .ToArrayAsync(cancellationToken);
        var grants = assignments.SelectMany(assignment => assignment.ProfileVersion!.Permissions
                .Select(permission => new PermissionGrant(permission.PermissionKey,
                    assignment.Scopes.Select(scope => new PermissionScope(scope.ContextId, scope.CameraId)).ToHashSet())))
            .GroupBy(grant => grant.PermissionKey, StringComparer.Ordinal)
            .Select(group => new PermissionGrant(group.Key, group.SelectMany(grant => grant.Scopes).ToHashSet())).ToArray();
        return new(true, isAdmin ? new HashSet<string>(["Administrator"], StringComparer.Ordinal) : new HashSet<string>(), grants);
    }

    public async Task<bool> IsAllowedAsync(Guid userId, PermissionRequest request, CancellationToken cancellationToken)
    {
        var subject = await GetSubjectAsync(userId, cancellationToken);
        return evaluator.IsAllowed(subject, request);
    }

    public async Task<bool> IsAllowedAnyCameraAsync(Guid userId, string permissionKey,
        CancellationToken cancellationToken)
    {
        var scopes = await database.CameraCatalog.AsNoTracking()
            .Where(camera => camera.IsEnabled && camera.Context.IsEnabled)
            .Select(camera => new PermissionScope(camera.ContextId, camera.CameraId)).ToArrayAsync(cancellationToken);
        foreach (var scope in scopes)
            if (await IsAllowedAsync(userId, new PermissionRequest(permissionKey, scope.ContextId, scope.CameraId),
                    cancellationToken))
                return true;
        return false;
    }

    private async Task EnsureAdmin(Guid actorId)
    {
        var actor = await users.FindByIdAsync(actorId.ToString());
        if (actor is null || !actor.IsActive || !await users.IsInRoleAsync(actor, "Administrator"))
            throw new UnauthorizedAccessException("A operação requer Administrator ativo.");
    }

    private static AccessProfileVersion NewVersion(Guid profileId, long version, Guid actorId,
        DateTimeOffset now, IReadOnlyCollection<string> keys, IPublicIdGenerator publicIds)
    {
        var result = new AccessProfileVersion
        {
            Id = Guid.NewGuid(),
            PublicId = publicIds.Generate(PublicIdPrefixes.AccessProfileVersionPrefix),
            ProfileId = profileId,
            Version = version,
            CreatedByUserId = actorId,
            CreatedAtUtc = now
        };
        foreach (var key in keys) result.Permissions.Add(new AccessProfilePermission
        { AccessProfileVersionId = result.Id, PermissionKey = key });
        return result;
    }

    private static string[] ValidatePermissions(IReadOnlyCollection<string> keys)
    {
        if (keys is null) throw new ArgumentException("Lista de permissões obrigatória.");
        var distinct = keys.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length != keys.Count || distinct.Any(key => !PermissionCatalog.TryGet(key, out var item) || !item.IsDelegable))
            throw new ArgumentException("O perfil contém permissão duplicada, desconhecida ou reservada.");
        return distinct;
    }

    private static ProfileAssignmentRequest[] ValidateAssignments(IReadOnlyCollection<ProfileAssignmentRequest> requested)
    {
        if (requested is null || requested.Count > 100) throw new ArgumentException("Conjunto de perfis inválido.");
        var values = requested.ToArray();
        if (values.Select(item => item.ProfileVersionId).Distinct().Count() != values.Length ||
            values.Any(item => item.ProfileVersionId == Guid.Empty || item.Scopes is null || item.Scopes.Count is < 1 or > 100))
            throw new ArgumentException("Atribuições devem ter perfil distinto e escopo explícito.");
        foreach (var scope in values.SelectMany(item => item.Scopes))
            if (string.IsNullOrWhiteSpace(scope.ContextId) || scope.ContextId.Length > 64 ||
                string.IsNullOrWhiteSpace(scope.CameraId) || scope.CameraId.Length > 64)
                throw new ArgumentException("Escopo de câmera inválido.");
        return values;
    }

    private static string ValidateName(string name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 128 || normalized.Any(char.IsControl))
            throw new ArgumentException("Nome do perfil inválido.");
        return normalized;
    }

    private static AccessProfileSummary Map(AccessProfile profile, AccessProfileVersion version, IReadOnlyList<string> keys) =>
        new(profile.PublicId!, version.PublicId!, profile.Name, profile.IsActive, profile.Version, profile.UpdatedAtUtc, keys);
}

public sealed record ProfileAssignmentRequest(Guid ProfileVersionId, IReadOnlyList<PermissionScope> Scopes);
