using GarageWatch.Infrastructure.Identity;
using GarageWatch.Application.Identity;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class IamTests
{
    [Fact]
    public async Task Bootstrap_persists_one_administrator_and_never_stores_the_password_as_plaintext()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var administrator = await users.FindByNameAsync("contract-user");

        Assert.NotNull(administrator);
        Assert.True(administrator.IsActive);
        Assert.NotEqual("Contract-Password-123!", administrator.PasswordHash);
        Assert.True(await users.IsInRoleAsync(administrator, "Administrator"));
        Assert.False(await users.IsInRoleAsync(administrator, "Viewer"));
        Assert.Equal(0, await database.CameraAccessGrants.CountAsync());
        var audit = await database.IamAuditEvents.SingleAsync();
        Assert.Equal("administrator.bootstrapped", audit.Action);
        Assert.Null(audit.ResourceId);

        if (OperatingSystem.IsLinux())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(factory.DatabasePath));
    }

    [Fact]
    public async Task Bootstrap_accepts_six_numeric_characters_for_local_administrator()
    {
        await using var factory = new ApiFactory(bootstrapPassword: "123456");
        using var client = factory.CreateClient();

        using var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "123456" });

        Assert.Equal(System.Net.HttpStatusCode.NoContent, login.StatusCode);
    }

    [Fact]
    public async Task Database_and_bootstrapped_identity_survive_host_restart_without_reseeding()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "garagewatch-tests", $"{Guid.NewGuid():N}.db");
        Guid administratorId;
        await using (var firstFactory = new ApiFactory(databasePath: databasePath))
        {
            using var firstClient = firstFactory.CreateClient();
            using var scope = firstFactory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var administrator = (await users.FindByNameAsync("contract-user"))!;
            administratorId = administrator.Id;
            await scope.ServiceProvider.GetRequiredService<IdentityAdministrationService>()
                .GrantCameraAsync(administratorId, administratorId, "garage", "garage-01");
        }

        await using (var restartedFactory = new ApiFactory(databasePath: databasePath,
            bootstrapUsername: null, bootstrapPassword: "replace-with-a-local-password-of-6-or-more-characters"))
        using (var restartedClient = restartedFactory.CreateClient())
        using (var scope = restartedFactory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var administrator = await users.FindByIdAsync(administratorId.ToString());
            Assert.NotNull(administrator);
            Assert.Equal("contract-user", administrator.UserName);
            Assert.Equal(2, await database.IamAuditEvents.CountAsync());
            Assert.Equal("garage-01", (await database.CameraAccessGrants.SingleAsync()).CameraId);
            Assert.Equal(2, await database.IamAuditEvents.OrderBy(entry => entry.OccurredAtUtc).CountAsync());
            Assert.Equal(System.Net.HttpStatusCode.NoContent, (await restartedClient.PostAsJsonAsync("/api/v1/auth/login",
                new { username = "contract-user", password = "Contract-Password-123!" })).StatusCode);
        }
    }

    [Fact]
    public async Task Last_administrator_cannot_be_demoted_or_deactivated_and_camera_grants_are_audited()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var administration = scope.ServiceProvider.GetRequiredService<IdentityAdministrationService>();
        var administrator = (await users.FindByNameAsync("contract-user"))!;

        await Assert.ThrowsAsync<InvalidOperationException>(() => administration.SetRolesAsync(
            administrator.Id, administrator.Id, ["Viewer"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => administration.DeactivateUserAsync(
            administrator.Id, administrator.Id));

        var viewer = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "grant-target",
            Email = "grant-target@example.invalid",
            IsActive = true
        };
        Assert.True((await users.CreateAsync(viewer, "Viewer-Password-123!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(viewer, "Viewer")).Succeeded);

        await administration.GrantCameraAsync(administrator.Id, viewer.Id, "garage", "garage-01");
        await administration.GrantCameraAsync(administrator.Id, viewer.Id, "garage", "garage-01");
        Assert.Single(await database.CameraAccessGrants.Where(grant => grant.RevokedAtUtc == null).ToListAsync());
        await administration.RevokeCameraAsync(administrator.Id, viewer.Id, "garage-01");
        var grant = await database.CameraAccessGrants.SingleAsync();
        Assert.NotNull(grant.RevokedAtUtc);
        Assert.Contains(await database.IamAuditEvents.Select(entry => entry.Action).ToListAsync(),
            action => action == "camera.access.granted");
        Assert.Contains(await database.IamAuditEvents.Select(entry => entry.Action).ToListAsync(),
            action => action == "camera.access.revoked");
    }

    [Fact]
    public async Task Role_change_revokes_existing_cookie_and_prevents_access_after_stamp_validation()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);

        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var administration = scope.ServiceProvider.GetRequiredService<IdentityAdministrationService>();
            var firstAdministrator = (await users.FindByNameAsync("contract-user"))!;
            var secondAdministrator = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = "second-admin",
                Email = "second-admin@example.invalid",
                IsActive = true
            };
            Assert.True((await users.CreateAsync(secondAdministrator, "Second-Admin-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(secondAdministrator, "Administrator")).Succeeded);
            await administration.DeactivateUserAsync(secondAdministrator.Id, firstAdministrator.Id);
        }

        using var revoked = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var denied = await client.GetAsync("/api/v1/cameras");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, denied.StatusCode);
        using var inactiveLogin = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, inactiveLogin.StatusCode);
    }

    [Fact]
    public async Task Repositories_support_pagination_and_with_details_loads_related_identity_summaries()
    {
        await using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = (await users.FindByNameAsync("contract-user"))!;
        var viewer = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "repository-viewer",
            Email = "repository-viewer@example.invalid",
            IsActive = true
        };
        Assert.True((await users.CreateAsync(viewer, "Viewer-Password-123!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(viewer, "Viewer")).Succeeded);
        await scope.ServiceProvider.GetRequiredService<IdentityAdministrationService>()
            .GrantCameraAsync(admin.Id, viewer.Id, "garage", "garage-01");

        var grantRepository = scope.ServiceProvider.GetRequiredService<ICameraAccessGrantRepository>();
        var bareGrant = Assert.IsType<CameraAccessGrantRecord>(await grantRepository.FindActiveAsync(viewer.Id, "garage-01"));
        Assert.Null(bareGrant.User);
        var detailedGrant = Assert.IsType<CameraAccessGrantRecord>(await grantRepository.GetAsync(bareGrant.Id, withDetails: true));
        Assert.Equal("repository-viewer", detailedGrant.User?.UserName);
        var page = await grantRepository.ListPaginatedAsync(1, 1, withDetails: true);
        Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);

        var auditRepository = scope.ServiceProvider.GetRequiredService<IIamAuditEventRepository>();
        var detailedAudit = Assert.Single(await auditRepository.GetAllAsync(withDetails: true),
            entry => entry.Action == "camera.access.granted");
        Assert.Equal("contract-user", detailedAudit.Actor?.UserName);
        Assert.Equal("repository-viewer", detailedAudit.Subject?.UserName);
        Assert.Single(await auditRepository.GetAllAsync(), entry => entry.Action == "administrator.bootstrapped");
    }

    [Fact]
    public async Task Unit_of_work_rolls_back_repository_changes_as_one_atomic_operation()
    {
        await using var factory = new ApiFactory();
        Guid administratorId;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            administratorId = (await users.FindByNameAsync("contract-user"))!.Id;
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IIamUnitOfWork>();
            var grants = scope.ServiceProvider.GetRequiredService<ICameraAccessGrantRepository>();
            var audit = scope.ServiceProvider.GetRequiredService<IIamAuditEventRepository>();

            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.ExecuteAtomicallyAsync(async _ =>
            {
                grants.Add(administratorId, "garage", "rollback-camera", administratorId, DateTimeOffset.UtcNow);
                audit.Append(administratorId, administratorId, "test.rollback", "rollback-camera", DateTimeOffset.UtcNow);
                await Task.Yield();
                throw new InvalidOperationException("force rollback");
            }));
        }

        using var verifyScope = factory.Services.CreateScope();
        var database = verifyScope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.Equal(0, await database.CameraAccessGrants.CountAsync());
        Assert.Equal(1, await database.IamAuditEvents.CountAsync());
    }

    [Fact]
    public async Task Concurrent_admin_demotions_are_serialized_and_preserve_the_last_administrator()
    {
        await using var factory = new ApiFactory();
        Guid firstAdminId;
        Guid secondAdminId;
        using (var setupScope = factory.Services.CreateScope())
        {
            var users = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            firstAdminId = (await users.FindByNameAsync("contract-user"))!.Id;
            var secondAdmin = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = "concurrent-admin",
                Email = "concurrent-admin@example.invalid",
                IsActive = true
            };
            Assert.True((await users.CreateAsync(secondAdmin, "Second-Admin-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(secondAdmin, "Administrator")).Succeeded);
            secondAdminId = secondAdmin.Id;
        }

        async Task<Exception?> DemoteSelfAsync(Guid adminId)
        {
            using var scope = factory.Services.CreateScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<IdentityAdministrationService>()
                    .SetRolesAsync(adminId, adminId, ["Viewer"]);
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        var outcomes = await Task.WhenAll(DemoteSelfAsync(firstAdminId), DemoteSelfAsync(secondAdminId));
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.IsType<InvalidOperationException>(Assert.Single(outcomes, outcome => outcome is not null));

        using var verifyScope = factory.Services.CreateScope();
        var usersAfter = verifyScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var activeAdministrators = 0;
        foreach (var adminId in new[] { firstAdminId, secondAdminId })
        {
            var user = await usersAfter.FindByIdAsync(adminId.ToString());
            if (user is not null && user.IsActive && await usersAfter.IsInRoleAsync(user, "Administrator"))
                activeAdministrators++;
        }
        Assert.Equal(1, activeAdministrators);
    }
}
