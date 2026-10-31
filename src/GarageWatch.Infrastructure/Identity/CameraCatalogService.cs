using GarageWatch.Application.Cameras;
using GarageWatch.Application.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed class CameraCatalogService(
    IamDbContext database,
    IIamUnitOfWork unitOfWork,
    IIamAuditEventRepository auditEvents,
    UserManager<ApplicationUser> users,
    AccessProfileService accessProfiles,
    IPublicIdGenerator publicIds,
    TimeProvider timeProvider) : ICameraCatalog
{
    public async Task<CatalogPage<CameraContextRecord>> ListContextsAsync(int page, int pageSize,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(page, pageSize);
        var query = database.CameraContexts.AsNoTracking().OrderBy(item => item.ContextId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new CameraContextRecord(item.PublicId!, item.Name, item.IsEnabled, item.Version,
                item.CreatedAtUtc, item.UpdatedAtUtc))
            .ToArrayAsync(cancellationToken);
        return new(items, page, pageSize, total);
    }

    public async Task<CatalogPage<CameraCatalogRecord>> ListCamerasAsync(int page, int pageSize,
        string? contextId = null, CancellationToken cancellationToken = default)
    {
        ValidatePage(page, pageSize);
        var query = database.CameraCatalog.AsNoTracking();
        if (contextId is not null) query = query.Where(item => item.Context.PublicId == contextId);
        var ordered = query.OrderBy(item => item.CameraId);
        var total = await ordered.CountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new CameraCatalogRecord(item.PublicId!, item.Context.PublicId!, item.Name, item.IsEnabled,
                item.Version, item.CreatedAtUtc, item.UpdatedAtUtc))
            .ToArrayAsync(cancellationToken);
        return new(items, page, pageSize, total);
    }

    public async Task<IReadOnlyList<CameraCatalogRecord>> ListVisibleCamerasAsync(Guid userId, bool isAdministrator,
        CancellationToken cancellationToken = default)
    {
        var cameras = database.CameraCatalog.AsNoTracking();
        if (isAdministrator)
        {
            return await cameras.OrderBy(item => item.CameraId)
                .Select(item => new CameraCatalogRecord(item.PublicId!, item.Context.PublicId!, item.Name, item.IsEnabled,
                    item.Version, item.CreatedAtUtc, item.UpdatedAtUtc))
                .ToArrayAsync(cancellationToken);
        }

        var enabled = await cameras
            .Where(camera => camera.IsEnabled && database.CameraContexts.Any(context =>
                context.ContextId == camera.ContextId && context.IsEnabled))
            .OrderBy(camera => camera.CameraId)
            .Select(camera => new
            {
                camera.CameraId,
                camera.ContextId,
                camera.PublicId,
                ContextPublicId = camera.Context.PublicId,
                camera.Name,
                camera.IsEnabled,
                camera.Version,
                camera.CreatedAtUtc,
                camera.UpdatedAtUtc
            })
            .ToArrayAsync(cancellationToken);
        var visible = new List<CameraCatalogRecord>();
        foreach (var camera in enabled)
        {
            var canList = await accessProfiles.IsAllowedAsync(userId,
                new GarageWatch.Application.Authorization.PermissionRequest("camera.list", camera.ContextId,
                    camera.CameraId), cancellationToken);
            var canViewLive = await accessProfiles.IsAllowedAsync(userId,
                new GarageWatch.Application.Authorization.PermissionRequest("live.video.view", camera.ContextId,
                    camera.CameraId), cancellationToken);
            if (canList || canViewLive)
                visible.Add(new CameraCatalogRecord(camera.PublicId!, camera.ContextPublicId!, camera.Name,
                    camera.IsEnabled, camera.Version, camera.CreatedAtUtc, camera.UpdatedAtUtc));
        }
        return visible;
    }

    public Task<CameraContextRecord> CreateContextAsync(Guid actorUserId, string name,
        CancellationToken cancellationToken = default) => unitOfWork.ExecuteAtomicallyAsync(async token =>
    {
        await EnsureAdministratorAsync(actorUserId);
        var normalizedName = ValidateName(name, nameof(name));
        var contextId = Guid.NewGuid().ToString("N");

        var now = timeProvider.GetUtcNow();
        var context = new CameraContext
        {
            ContextId = contextId,
            PublicId = publicIds.Generate(PublicIdPrefixes.ContextPrefix),
            Name = normalizedName,
            IsEnabled = true,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.CameraContexts.Add(context);
        Record(actorUserId, "camera_context.created", contextId);
        return Map(context);
    }, cancellationToken);

    public Task<CameraContextRecord> UpdateContextAsync(Guid actorUserId, string contextId, long expectedVersion,
        string name, bool isEnabled, CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            await EnsureAdministratorAsync(actorUserId);
            if (!PublicIdFormat.IsValid(contextId, PublicIdPrefixes.ContextPrefix))
                throw new CatalogNotFoundException("Contexto não encontrado.");
            ValidateVersion(expectedVersion);
            var context = await database.CameraContexts.SingleOrDefaultAsync(item => item.PublicId == contextId, token)
                ?? throw new CatalogNotFoundException("Contexto não encontrado.");
            EnsureVersion(context.Version, expectedVersion);
            if (!isEnabled && await database.CameraCatalog.AnyAsync(
                     item => item.ContextId == context.ContextId && item.IsEnabled, token))
                throw new CatalogConflictException("Desative as câmeras do contexto antes de desativá-lo.");

            var normalizedName = ValidateName(name, nameof(name));
            if (context.Name == normalizedName && context.IsEnabled == isEnabled) return Map(context);
            context.Name = normalizedName;
            context.IsEnabled = isEnabled;
            context.Version++;
            context.UpdatedAtUtc = timeProvider.GetUtcNow();
            Record(actorUserId, "camera_context.updated", context.ContextId);
            return Map(context);
        }, cancellationToken);

    public Task<CameraContextRecord> DeactivateContextAsync(Guid actorUserId, string contextId, long expectedVersion,
        CancellationToken cancellationToken = default) => unitOfWork.ExecuteAtomicallyAsync(async token =>
    {
        await EnsureAdministratorAsync(actorUserId);
        if (!PublicIdFormat.IsValid(contextId, PublicIdPrefixes.ContextPrefix))
            throw new CatalogNotFoundException("Contexto não encontrado.");
        ValidateVersion(expectedVersion);
        var context = await database.CameraContexts.SingleOrDefaultAsync(item => item.PublicId == contextId, token)
            ?? throw new CatalogNotFoundException("Contexto não encontrado.");
        EnsureVersion(context.Version, expectedVersion);
        if (await database.CameraCatalog.AnyAsync(item => item.ContextId == context.ContextId && item.IsEnabled, token))
            throw new CatalogConflictException("Desative as câmeras do contexto antes de desativá-lo.");
        if (context.IsEnabled)
        {
            context.IsEnabled = false;
            context.Version++;
            context.UpdatedAtUtc = timeProvider.GetUtcNow();
            Record(actorUserId, "camera_context.deactivated", context.ContextId);
        }
        return Map(context);
    }, cancellationToken);

    public Task<CameraCatalogRecord> CreateCameraAsync(Guid actorUserId, string contextId,
        string name, CancellationToken cancellationToken = default) => unitOfWork.ExecuteAtomicallyAsync(async token =>
    {
        await EnsureAdministratorAsync(actorUserId);
        if (!PublicIdFormat.IsValid(contextId, PublicIdPrefixes.ContextPrefix))
            throw new CatalogNotFoundException("Contexto não encontrado.");
        var normalizedName = ValidateName(name, nameof(name));
        var context = await database.CameraContexts.SingleOrDefaultAsync(item => item.PublicId == contextId, token)
            ?? throw new CatalogNotFoundException("Contexto não encontrado.");
        if (!context.IsEnabled) throw new CatalogConflictException("O contexto está desativado.");
        var cameraId = Guid.NewGuid().ToString("N");

        var now = timeProvider.GetUtcNow();
        var camera = new CameraCatalogEntry
        {
            CameraId = cameraId,
            PublicId = publicIds.Generate(PublicIdPrefixes.CameraPrefix),
            ContextId = context.ContextId,
            Context = context,
            Name = normalizedName,
            IsEnabled = true,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.CameraCatalog.Add(camera);
        Record(actorUserId, "camera_catalog.created", cameraId);
        return Map(camera);
    }, cancellationToken);

    public Task<CameraCatalogRecord> UpdateCameraAsync(Guid actorUserId, string cameraId, long expectedVersion,
        string contextId, string name, bool isEnabled, CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            await EnsureAdministratorAsync(actorUserId);
            if (!PublicIdFormat.IsValid(cameraId, PublicIdPrefixes.CameraPrefix) ||
                !PublicIdFormat.IsValid(contextId, PublicIdPrefixes.ContextPrefix))
                throw new CatalogNotFoundException("Câmera ou contexto não encontrado.");
            ValidateVersion(expectedVersion);
            var camera = await database.CameraCatalog.Include(item => item.Context)
                .SingleOrDefaultAsync(item => item.PublicId == cameraId, token)
                ?? throw new CatalogNotFoundException("Câmera não encontrada.");
            EnsureVersion(camera.Version, expectedVersion);
            var context = await database.CameraContexts.SingleOrDefaultAsync(item => item.PublicId == contextId, token)
                ?? throw new CatalogNotFoundException("Contexto não encontrado.");
            if (isEnabled && !context.IsEnabled) throw new CatalogConflictException("O contexto está desativado.");

            var normalizedName = ValidateName(name, nameof(name));
            if (camera.ContextId == context.ContextId && camera.Name == normalizedName && camera.IsEnabled == isEnabled)
                return Map(camera);
            camera.ContextId = context.ContextId;
            camera.Context = context;
            camera.Name = normalizedName;
            camera.IsEnabled = isEnabled;
            camera.Version++;
            camera.UpdatedAtUtc = timeProvider.GetUtcNow();
            Record(actorUserId, isEnabled ? "camera_catalog.updated" : "camera_catalog.deactivated", cameraId);
            return Map(camera);
        }, cancellationToken);

    public Task<CameraCatalogRecord> DeactivateCameraAsync(Guid actorUserId, string cameraId, long expectedVersion,
        CancellationToken cancellationToken = default) => unitOfWork.ExecuteAtomicallyAsync(async token =>
    {
        await EnsureAdministratorAsync(actorUserId);
        if (!PublicIdFormat.IsValid(cameraId, PublicIdPrefixes.CameraPrefix))
            throw new CatalogNotFoundException("Câmera não encontrada.");
        ValidateVersion(expectedVersion);
        var camera = await database.CameraCatalog.Include(item => item.Context)
            .SingleOrDefaultAsync(item => item.PublicId == cameraId, token)
            ?? throw new CatalogNotFoundException("Câmera não encontrada.");
        EnsureVersion(camera.Version, expectedVersion);
        if (camera.IsEnabled)
        {
            camera.IsEnabled = false;
            camera.Version++;
            camera.UpdatedAtUtc = timeProvider.GetUtcNow();
            Record(actorUserId, "camera_catalog.deactivated", cameraId);
        }
        return Map(camera);
    }, cancellationToken);

    private async Task EnsureAdministratorAsync(Guid actorUserId)
    {
        var actor = await users.FindByIdAsync(actorUserId.ToString());
        if (actor is null || !actor.IsActive || !await users.IsInRoleAsync(actor, "Administrator"))
            throw new UnauthorizedAccessException("A operação requer administrador ativo.");
    }

    private void Record(Guid actorUserId, string action, string resourceId) =>
        auditEvents.Append(actorUserId, null, action, resourceId, timeProvider.GetUtcNow());

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new CatalogValidationException($"{parameterName} inválido.");
    }

    private static string ValidateName(string value, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > 128 || normalized.Any(char.IsControl))
            throw new CatalogValidationException($"{parameterName} inválido.");
        return normalized;
    }

    private static void ValidatePage(int page, int pageSize)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new CatalogValidationException("Paginação inválida.");
    }

    private static void ValidateVersion(long version)
    {
        if (version < 1) throw new CatalogValidationException("Versão inválida.");
    }

    private static void EnsureVersion(long actual, long expected)
    {
        if (actual != expected) throw new CatalogConflictException("O registro mudou; atualize a consulta e tente novamente.");
    }

    private static CameraContextRecord Map(CameraContext context) =>
        new(context.PublicId!, context.Name, context.IsEnabled, context.Version, context.CreatedAtUtc, context.UpdatedAtUtc);

    private static CameraCatalogRecord Map(CameraCatalogEntry camera) =>
        new(camera.PublicId!, camera.Context.PublicId!, camera.Name, camera.IsEnabled, camera.Version,
            camera.CreatedAtUtc, camera.UpdatedAtUtc);
}
