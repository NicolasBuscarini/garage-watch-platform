using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Runtime.InteropServices;
using System.Text.Json;
using GarageWatch.Infrastructure.Capture;

namespace GarageWatch.Infrastructure.Identity;

public sealed class IamDatabaseInitializer(
    IamDbContext database,
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    IdentityBootstrapOptions bootstrap,
    GarageWatch.Infrastructure.Capture.CaptureOptions captureOptions,
    IPublicIdGenerator publicIds,
    TimeProvider timeProvider)
{
    private static readonly string[] KnownRoles = ["User", "Viewer", "Administrator"];
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var databasePath = database.Database.GetDbConnection().DataSource;
        var databaseDirectory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(databaseDirectory))
        {
            Directory.CreateDirectory(databaseDirectory);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(databaseDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        await database.Database.MigrateAsync(cancellationToken);
        await BackfillPublicIdsAsync(cancellationToken);
        await BackfillClipManifestPublicIdsAsync(cancellationToken);
        if (OperatingSystem.IsLinux() && File.Exists(databasePath))
            File.SetUnixFileMode(databasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);

        foreach (var roleName in KnownRoles)
        {
            if (await roles.RoleExistsAsync(roleName)) continue;
            var result = await roles.CreateAsync(new IdentityRole<Guid>(roleName) { Id = Guid.NewGuid() });
            if (!result.Succeeded) throw new InvalidOperationException("Não foi possível inicializar os papéis do IAM.");
        }

        var userCount = await users.Users.CountAsync(cancellationToken);
        if (userCount == 0)
        {
            if (!bootstrap.IsConfigured)
                throw new InvalidOperationException("O banco IAM está vazio; configure AUTH_USERNAME e AUTH_PASSWORD para provisionar o primeiro administrador.");
            bootstrap.ValidateForBootstrap();

            var administrator = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = bootstrap.Username!,
                Email = bootstrap.Email,
                EmailConfirmed = false,
                LockoutEnabled = true,
                IsActive = true
            };
            var created = await users.CreateAsync(administrator, bootstrap.Password!);
            if (!created.Succeeded) throw new InvalidOperationException("Não foi possível provisionar o administrador inicial.");

            var assigned = await users.AddToRoleAsync(administrator, "Administrator");
            if (!assigned.Succeeded) throw new InvalidOperationException("Não foi possível atribuir o papel inicial de administrador.");

            database.IamAuditEvents.Add(new IamAuditEvent
            {
                Id = Guid.NewGuid(),
                ActorUserId = administrator.Id,
                SubjectUserId = administrator.Id,
                Action = "administrator.bootstrapped",
                OccurredAtUtc = timeProvider.GetUtcNow()
            });
            await database.SaveChangesAsync(cancellationToken);
        }

        var hasAdministrator = await (from user in users.Users
                                      join userRole in database.UserRoles on user.Id equals userRole.UserId
                                      join role in database.Roles on userRole.RoleId equals role.Id
                                      where role.Name == "Administrator" && user.IsActive
                                      select user.Id).AnyAsync(cancellationToken);
        if (!hasAdministrator)
            throw new InvalidOperationException("O IAM não possui administrador ativo; restaure um backup válido ou execute recuperação operacional.");

        if (!await database.CameraCatalog.AnyAsync(cancellationToken))
        {
            var context = new CameraContext
            {
                ContextId = "garage",
                Name = "Garagem",
                IsEnabled = true,
                Version = 1,
                CreatedAtUtc = timeProvider.GetUtcNow(),
                UpdatedAtUtc = timeProvider.GetUtcNow()
            };
            database.CameraContexts.Add(context);
            database.CameraCatalog.Add(new CameraCatalogEntry
            {
                CameraId = captureOptions.CameraId,
                ContextId = context.ContextId,
                Name = "Câmera da garagem",
                IsEnabled = true,
                Version = 1,
                CreatedAtUtc = context.CreatedAtUtc,
                UpdatedAtUtc = context.UpdatedAtUtc
            });
            await database.SaveChangesAsync(cancellationToken);
        }

        await MigrateLegacyCameraGrantsAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task BackfillPublicIdsAsync(CancellationToken cancellationToken)
    {
        AssignPublicIds(await users.Users.ToArrayAsync(cancellationToken), PublicIdPrefixes.UserPrefix);
        AssignPublicIds(await database.CameraContexts.ToArrayAsync(cancellationToken), PublicIdPrefixes.ContextPrefix);
        AssignPublicIds(await database.CameraCatalog.ToArrayAsync(cancellationToken), PublicIdPrefixes.CameraPrefix);
        AssignPublicIds(await database.AccessProfiles.ToArrayAsync(cancellationToken), PublicIdPrefixes.AccessProfilePrefix);
        AssignPublicIds(await database.AccessProfileVersions.ToArrayAsync(cancellationToken), PublicIdPrefixes.AccessProfileVersionPrefix);
        AssignPublicIds(await database.UserAccessProfileAssignments.ToArrayAsync(cancellationToken), PublicIdPrefixes.ProfileAssignmentPrefix);
        AssignPublicIds(await database.AccessRequests.ToArrayAsync(cancellationToken), PublicIdPrefixes.AccessRequestPrefix);
        AssignPublicIds(await database.ClipAnalysisJobs.ToArrayAsync(cancellationToken), PublicIdPrefixes.ClipAnalysisJobPrefix);

        await database.SaveChangesAsync(cancellationToken);
        var missingIds = await users.Users.AnyAsync(item => item.PublicId == null, cancellationToken)
            || await database.CameraContexts.AnyAsync(item => item.PublicId == null, cancellationToken)
            || await database.CameraCatalog.AnyAsync(item => item.PublicId == null, cancellationToken)
            || await database.AccessProfiles.AnyAsync(item => item.PublicId == null, cancellationToken)
            || await database.AccessProfileVersions.AnyAsync(item => item.PublicId == null, cancellationToken)
            || await database.UserAccessProfileAssignments.AnyAsync(item => item.PublicId == null, cancellationToken)
            || await database.AccessRequests.AnyAsync(item => item.PublicId == null, cancellationToken)
            || await database.ClipAnalysisJobs.AnyAsync(item => item.PublicId == null, cancellationToken);
        if (missingIds) throw new InvalidOperationException("Backfill de IDs públicos incompleto.");
    }

    private void AssignPublicIds<TEntity>(IReadOnlyCollection<TEntity> entities, string prefix)
        where TEntity : class, IHasPublicId
    {
        var reservedIds = entities.Where(entity => entity.PublicId is not null)
            .Select(entity => entity.PublicId!).ToHashSet(StringComparer.Ordinal);
        if (entities.Any(entity => entity.PublicId is not null && !PublicIdFormat.IsValid(entity.PublicId, prefix)))
            throw new InvalidDataException("Banco IAM contém ID público com formato inválido.");
        foreach (var entity in entities.Where(entity => entity.PublicId is null))
            entity.PublicId = PublicIdAllocator.GenerateUnique(publicIds, prefix, reservedIds);
    }

    private async Task BackfillClipManifestPublicIdsAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(captureOptions.SpoolDirectory, "clips", "manifests");
        if (!Directory.Exists(directory)) return;
        var reservedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClipManifestV1? manifest;
            try { manifest = JsonSerializer.Deserialize<ClipManifestV1>(await File.ReadAllTextAsync(path, cancellationToken), ManifestJsonOptions); }
            catch (JsonException) { continue; }
            if (manifest is not { SchemaVersion: "1.0" } ||
                Path.GetFileName(path) != manifest.ClipId + ".json" || !LocalSegmentSpool.IsSafeSegmentId(manifest.ClipId)) continue;
            if (PublicIdFormat.IsValid(manifest.PublicId, PublicIdPrefixes.ClipPrefix) &&
                reservedIds.Add(manifest.PublicId!)) continue;
            var updated = manifest with { PublicId = ClipPublicIdAllocator.GenerateUnique(publicIds, reservedIds) };
            var temporary = path + ".public-id.tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(updated, ManifestJsonOptions), cancellationToken);
            File.Move(temporary, path, true);
        }
    }

    private async Task MigrateLegacyCameraGrantsAsync(CancellationToken cancellationToken)
    {
        const string migrationKey = "legacy-camera-grants-to-access-profile-v1";
        if (await database.IamMigrationStates.AnyAsync(item => item.Key == migrationKey, cancellationToken)) return;

        var now = timeProvider.GetUtcNow();
        var profile = await database.AccessProfiles.SingleOrDefaultAsync(item => item.Name == "Live somente vídeo",
            cancellationToken);
        AccessProfileVersion currentVersion;
        if (profile is null)
        {
            profile = new AccessProfile
            {
                Id = Guid.NewGuid(),
                Name = "Live somente vídeo",
                IsActive = true,
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            currentVersion = new AccessProfileVersion
            {
                Id = Guid.NewGuid(),
                ProfileId = profile.Id,
                Version = 1,
                CreatedAtUtc = now,
                CreatedByUserId = Guid.Empty
            };
            currentVersion.Permissions.Add(new AccessProfilePermission
            {
                AccessProfileVersionId = currentVersion.Id,
                PermissionKey = "live.video.view"
            });
            database.AccessProfiles.Add(profile);
            database.AccessProfileVersions.Add(currentVersion);
        }
        else
            currentVersion = await database.AccessProfileVersions.SingleAsync(item =>
                item.ProfileId == profile.Id && item.Version == profile.Version, cancellationToken);
        var grants = await database.CameraAccessGrants.AsNoTracking()
            .Where(grant => grant.RevokedAtUtc == null)
            .OrderBy(grant => grant.UserId).ThenBy(grant => grant.ContextId).ThenBy(grant => grant.CameraId)
            .ToArrayAsync(cancellationToken);
        foreach (var group in grants.GroupBy(grant => grant.UserId))
        {
            var user = await users.FindByIdAsync(group.Key.ToString());
            if (user is null || !user.IsActive || await users.IsInRoleAsync(user, "Administrator")) continue;
            var assignment = new UserAccessProfileAssignment
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                AccessProfileVersionId = currentVersion.Id,
                AssignedByUserId = Guid.Empty,
                AssignedAtUtc = now
            };
            foreach (var grant in group)
                assignment.Scopes.Add(new UserAccessProfileScope
                {
                    AssignmentId = assignment.Id,
                    ContextId = grant.ContextId,
                    CameraId = grant.CameraId
                });
            database.UserAccessProfileAssignments.Add(assignment);
        }
        database.IamMigrationStates.Add(new IamMigrationState { Key = migrationKey, CompletedAtUtc = now });
        await database.SaveChangesAsync(cancellationToken);
    }
}
