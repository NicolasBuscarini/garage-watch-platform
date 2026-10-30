using GarageWatch.Application.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed class IdentityAdministrationService(
    IIamUnitOfWork unitOfWork,
    ICameraAccessGrantRepository grants,
    IIamAuditEventRepository auditEvents,
    IamDbContext database,
    UserManager<ApplicationUser> users,
    TimeProvider timeProvider)
{
    private static readonly HashSet<string> AssignableRoles = ["User", "Viewer", "Administrator"];

    public Task SetRolesAsync(Guid actorUserId, Guid subjectUserId, IReadOnlyCollection<string> requestedRoles,
        CancellationToken cancellationToken = default) => unitOfWork.ExecuteAtomicallyAsync(async token =>
    {
        await EnsureAdministratorAsync(actorUserId);
        var targetRoles = requestedRoles.ToHashSet(StringComparer.Ordinal);
        if (targetRoles.Count == 0 || targetRoles.Any(role => !AssignableRoles.Contains(role)))
            throw new ArgumentException("O conjunto de papéis solicitado não é válido.", nameof(requestedRoles));

        var user = await users.FindByIdAsync(subjectUserId.ToString())
            ?? throw new KeyNotFoundException("Conta não encontrada.");
        if (!user.IsActive) throw new InvalidOperationException("Conta desativada.");

        var currentRoles = await users.GetRolesAsync(user);
        var removesAdministrator = currentRoles.Contains("Administrator") && !targetRoles.Contains("Administrator");
        if (removesAdministrator && !await HasOtherAdministratorAsync(subjectUserId, token))
            throw new InvalidOperationException("Não é permitido remover o último administrador ativo.");

        var remove = currentRoles.Except(targetRoles, StringComparer.Ordinal).ToArray();
        var add = targetRoles.Except(currentRoles, StringComparer.Ordinal).ToArray();
        if (remove.Length > 0 && !(await users.RemoveFromRolesAsync(user, remove)).Succeeded)
            throw new InvalidOperationException("Não foi possível revogar os papéis da conta.");
        if (add.Length > 0 && !(await users.AddToRolesAsync(user, add)).Succeeded)
            throw new InvalidOperationException("Não foi possível atribuir os papéis à conta.");
        await InvalidateSessionsAsync(user);
        Record(actorUserId, subjectUserId, "user.roles.updated");
    }, cancellationToken);

    public Task DeactivateUserAsync(Guid actorUserId, Guid subjectUserId,
        CancellationToken cancellationToken = default) => unitOfWork.ExecuteAtomicallyAsync(async token =>
    {
        await EnsureAdministratorAsync(actorUserId);
        var user = await users.FindByIdAsync(subjectUserId.ToString())
            ?? throw new KeyNotFoundException("Conta não encontrada.");
        if (!user.IsActive) return;

        var currentRoles = await users.GetRolesAsync(user);
        if (currentRoles.Contains("Administrator") && !await HasOtherAdministratorAsync(subjectUserId, token))
            throw new InvalidOperationException("Não é permitido desativar o último administrador ativo.");

        var revokedAtUtc = timeProvider.GetUtcNow();
        var activeGrants = await database.CameraAccessGrants
            .Where(grant => grant.UserId == subjectUserId && grant.RevokedAtUtc == null)
            .ToListAsync(token);
        foreach (var grant in activeGrants)
        {
            grant.RevokedAtUtc = revokedAtUtc;
            Record(actorUserId, subjectUserId, "camera.access.revoked", grant.CameraId);
        }

        var activeProfileAssignments = await database.UserAccessProfileAssignments
            .Where(assignment => assignment.UserId == subjectUserId && assignment.RevokedAtUtc == null)
            .ToListAsync(token);
        foreach (var assignment in activeProfileAssignments)
            assignment.RevokedAtUtc = revokedAtUtc;

        user.IsActive = false;
        var updated = await users.UpdateAsync(user);
        if (!updated.Succeeded) throw new InvalidOperationException("Não foi possível desativar a conta.");
        await InvalidateSessionsAsync(user);
        Record(actorUserId, subjectUserId, "user.deactivated");
    }, cancellationToken);

    public Task GrantCameraAsync(Guid actorUserId, Guid subjectUserId, string contextId, string cameraId,
        CancellationToken cancellationToken = default) => unitOfWork.ExecuteAtomicallyAsync(async token =>
    {
        await EnsureAdministratorAsync(actorUserId);
        ValidateIdentifier(contextId, nameof(contextId));
        ValidateIdentifier(cameraId, nameof(cameraId));
        var user = await users.FindByIdAsync(subjectUserId.ToString())
            ?? throw new KeyNotFoundException("Conta não encontrada.");
        if (!user.IsActive) throw new InvalidOperationException("Conta desativada.");

        var camera = await database.CameraCatalog.AsNoTracking()
            .Where(item => item.CameraId == cameraId && item.ContextId == contextId && item.IsEnabled)
            .Join(database.CameraContexts.AsNoTracking().Where(item => item.IsEnabled),
                item => item.ContextId, context => context.ContextId, (item, _) => item)
            .SingleOrDefaultAsync(token);
        if (camera is null) throw new InvalidOperationException("A câmera não está habilitada no catálogo.");

        if (await grants.FindActiveAsync(subjectUserId, cameraId, cancellationToken: token) is not null) return;

        grants.Add(subjectUserId, contextId, cameraId, actorUserId, timeProvider.GetUtcNow());
        await InvalidateSessionsAsync(user);
        Record(actorUserId, subjectUserId, "camera.access.granted", cameraId);
    }, cancellationToken);

    public Task RevokeCameraAsync(Guid actorUserId, Guid subjectUserId, string cameraId,
        CancellationToken cancellationToken = default) => unitOfWork.ExecuteAtomicallyAsync(async token =>
    {
        await EnsureAdministratorAsync(actorUserId);
        if (!await grants.RevokeActiveAsync(subjectUserId, cameraId, timeProvider.GetUtcNow(), token)) return;
        var user = await users.FindByIdAsync(subjectUserId.ToString());
        if (user is not null) await InvalidateSessionsAsync(user);
        Record(actorUserId, subjectUserId, "camera.access.revoked", cameraId);
    }, cancellationToken);

    private async Task<bool> HasOtherAdministratorAsync(Guid excludedUserId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var administrators = await users.GetUsersInRoleAsync("Administrator");
        return administrators.Any(user => user.Id != excludedUserId && user.IsActive);
    }

    private async Task EnsureAdministratorAsync(Guid actorUserId)
    {
        var actor = await users.FindByIdAsync(actorUserId.ToString());
        if (actor is null || !actor.IsActive || !await users.IsInRoleAsync(actor, "Administrator"))
            throw new UnauthorizedAccessException("A operação requer administrador ativo.");
    }

    private async Task InvalidateSessionsAsync(ApplicationUser user)
    {
        var result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded) throw new InvalidOperationException("Não foi possível revogar as sessões da conta.");
    }

    private void Record(Guid actorUserId, Guid subjectUserId, string action, string? resourceId = null) =>
        auditEvents.Append(actorUserId, subjectUserId, action, resourceId, timeProvider.GetUtcNow());

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(char.IsControl))
            throw new ArgumentException("Identificador fora dos limites.", parameterName);
    }
}
