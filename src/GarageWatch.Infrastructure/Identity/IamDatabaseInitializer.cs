using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Runtime.InteropServices;

namespace GarageWatch.Infrastructure.Identity;

public sealed class IamDatabaseInitializer(
    IamDbContext database,
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole<Guid>> roles,
    IdentityBootstrapOptions bootstrap,
    GarageWatch.Infrastructure.Capture.CaptureOptions captureOptions,
    TimeProvider timeProvider)
{
    private static readonly string[] KnownRoles = ["User", "Viewer", "Administrator"];

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

        await transaction.CommitAsync(cancellationToken);
    }
}
