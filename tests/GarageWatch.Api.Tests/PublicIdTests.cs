using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Capture;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class PublicIdTests
{
    [Fact]
    public async Task Unique_public_id_collision_retries_the_whole_transaction_without_partial_rows()
    {
        var generator = new ScriptedPublicIdGenerator();
        await using var factory = new ApiFactory(publicIdGenerator: generator);
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        Assert.Same(generator, services.GetRequiredService<IPublicIdGenerator>());
        var administrator = await services.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByNameAsync("contract-user");
        var database = services.GetRequiredService<IamDbContext>();
        var existingPublicId = await database.AccessProfiles.Select(item => item.PublicId).FirstAsync();
        var generatedBefore = generator.Generated(PublicIdPrefixes.AccessProfilePrefix);
        var profileCount = await database.AccessProfiles.CountAsync();
        var versionCount = await database.AccessProfileVersions.CountAsync();

        generator.Enqueue(PublicIdPrefixes.AccessProfilePrefix, existingPublicId!);
        generator.Enqueue(PublicIdPrefixes.AccessProfilePrefix, "PRF999999999999");

        var created = await services.GetRequiredService<AccessProfileService>().CreateAsync(administrator!.Id,
            "Profile after collision", ["live.video.view"], CancellationToken.None);

        Assert.Equal("PRF999999999999", created.PublicId);
        Assert.Equal(profileCount + 1, await database.AccessProfiles.CountAsync());
        Assert.Equal(versionCount + 1, await database.AccessProfileVersions.CountAsync());
        Assert.Equal(generatedBefore + 2, generator.Generated(PublicIdPrefixes.AccessProfilePrefix));
    }

    [Fact]
    public async Task Exhausted_public_id_collision_retries_leave_no_profile_version_or_audit_row()
    {
        var generator = new ScriptedPublicIdGenerator();
        await using var factory = new ApiFactory(publicIdGenerator: generator);
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var administrator = await services.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByNameAsync("contract-user");
        var database = services.GetRequiredService<IamDbContext>();
        var existingPublicId = await database.AccessProfiles.Select(item => item.PublicId).FirstAsync();
        var generatedBefore = generator.Generated(PublicIdPrefixes.AccessProfilePrefix);
        var profileCount = await database.AccessProfiles.CountAsync();
        var versionCount = await database.AccessProfileVersions.CountAsync();
        var auditCount = await database.IamAuditEvents.CountAsync();
        for (var attempt = 0; attempt < 3; attempt++)
            generator.Enqueue(PublicIdPrefixes.AccessProfilePrefix, existingPublicId!);

        await Assert.ThrowsAsync<DbUpdateException>(() => services.GetRequiredService<AccessProfileService>()
            .CreateAsync(administrator!.Id, "Must roll back", ["live.video.view"], CancellationToken.None));

        Assert.Equal(profileCount, await database.AccessProfiles.CountAsync());
        Assert.Equal(versionCount, await database.AccessProfileVersions.CountAsync());
        Assert.Equal(auditCount, await database.IamAuditEvents.CountAsync());
        Assert.Equal(generatedBefore + 3, generator.Generated(PublicIdPrefixes.AccessProfilePrefix));
    }

    [Fact]
    public async Task Concurrent_profile_creations_publish_distinct_public_ids()
    {
        await using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var administrator = await services.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByNameAsync("contract-user");
        var profiles = services.GetRequiredService<AccessProfileService>();

        var results = await Task.WhenAll(
            profiles.CreateAsync(administrator!.Id, "Concurrent profile A", ["live.video.view"], CancellationToken.None),
            profiles.CreateAsync(administrator.Id, "Concurrent profile B", ["events.read"], CancellationToken.None));

        Assert.Equal(2, results.Select(item => item.PublicId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, results.Select(item => item.CurrentVersionPublicId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(results, item => Assert.True(PublicIdFormat.IsValid(item.PublicId, PublicIdPrefixes.AccessProfilePrefix)));
    }

    [Fact]
    public async Task Identity_insert_retries_a_public_id_collision_and_persists_the_replacement()
    {
        var generator = new ScriptedPublicIdGenerator();
        await using var factory = new ApiFactory(publicIdGenerator: generator);
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var existingPublicId = await services.GetRequiredService<IamDbContext>().Users
            .Select(item => item.PublicId).FirstAsync();
        generator.Enqueue(PublicIdPrefixes.UserPrefix, existingPublicId!);
        var user = new ApplicationUser { UserName = "public-id-retry-user", IsActive = true };

        var result = await users.CreateAsync(user, "Retry-Password-123!");

        Assert.True(result.Succeeded);
        Assert.NotEqual(existingPublicId, user.PublicId);
        Assert.True(PublicIdFormat.IsValid(user.PublicId, PublicIdPrefixes.UserPrefix));
        Assert.Equal(user.PublicId, await services.GetRequiredService<IamDbContext>().Users
            .Where(item => item.Id == user.Id).Select(item => item.PublicId).SingleAsync());
    }

    [Fact]
    public void Production_generator_returns_a_fixed_width_typed_random_identifier()
    {
        var generator = new PublicIdGenerator();
        var ids = Enumerable.Range(0, 100).Select(_ => generator.Generate(PublicIdPrefixes.CameraPrefix)).ToArray();

        Assert.All(ids, id => Assert.True(PublicIdFormat.IsValid(id, PublicIdPrefixes.CameraPrefix)));
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Clip_manifest_id_allocator_retries_collisions_and_fails_after_bounded_attempts()
    {
        var collision = "CLP000000000001";
        var unique = "CLP000000000002";
        var reserved = new HashSet<string>([collision], StringComparer.Ordinal);
        var generator = new ScriptedPublicIdGenerator();
        generator.Enqueue(PublicIdPrefixes.ClipPrefix, collision);
        generator.Enqueue(PublicIdPrefixes.ClipPrefix, unique);

        Assert.Equal(unique, ClipPublicIdAllocator.GenerateUnique(generator, reserved));
        Assert.Contains(unique, reserved);

        var exhaustedGenerator = new ScriptedPublicIdGenerator();
        for (var attempt = 0; attempt < 3; attempt++)
            exhaustedGenerator.Enqueue(PublicIdPrefixes.ClipPrefix, collision);
        Assert.Throws<InvalidOperationException>(() =>
            ClipPublicIdAllocator.GenerateUnique(exhaustedGenerator, reserved));
        Assert.Equal(3, exhaustedGenerator.Generated(PublicIdPrefixes.ClipPrefix));
    }

    [Fact]
    public async Task Upgrade_backfills_legacy_database_rows_and_clip_manifests_without_changing_internal_keys()
    {
        await using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var database = services.GetRequiredService<IamDbContext>();
        var camera = await database.CameraCatalog.AsNoTracking().Select(item => new { item.CameraId, item.PublicId }).SingleAsync();
        var context = await database.CameraContexts.AsNoTracking().Select(item => new { item.ContextId, item.PublicId }).SingleAsync();
        var profileId = await database.AccessProfiles.Select(item => item.Id).FirstAsync();
        var profileVersionId = await database.AccessProfileVersions.Where(item => item.ProfileId == profileId)
            .Select(item => item.Id).FirstAsync();
        var administratorId = await database.Users.Select(item => item.Id).FirstAsync();
        var now = DateTimeOffset.UtcNow;
        var assignment = new UserAccessProfileAssignment
        {
            Id = Guid.NewGuid(),
            UserId = administratorId,
            AccessProfileVersionId = profileVersionId,
            AssignedByUserId = administratorId,
            AssignedAtUtc = now
        };
        var accessRequest = new AccessRequest
        {
            Id = Guid.NewGuid(),
            UserId = administratorId,
            IdempotencyKey = Guid.NewGuid(),
            Purpose = AccessRequestPurposes.LiveMonitoring,
            Status = "pending",
            CreatedAtUtc = now
        };
        var analysisJob = new ClipAnalysisJob
        {
            Id = Guid.NewGuid(),
            ClipId = Guid.NewGuid().ToString("N"),
            ContextId = context.ContextId,
            CameraId = camera.CameraId,
            IdempotencyKey = Guid.NewGuid(),
            AnalysisVersion = "contract-v1",
            PolicyVersion = 1,
            State = "analysis_pending",
            RequestKind = "manual",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.AddRange(assignment, accessRequest, analysisJob);
        await database.SaveChangesAsync();
        var options = services.GetRequiredService<CaptureOptions>();
        var manifests = Path.Combine(options.SpoolDirectory, "clips", "manifests");
        Directory.CreateDirectory(manifests);
        var clipKey = Guid.NewGuid().ToString("N");
        var manifestPath = Path.Combine(manifests, clipKey + ".json");
        var manifestNow = DateTimeOffset.UtcNow;
        var legacyManifest = new ClipManifestV1("1.0", clipKey, camera.CameraId, manifestNow, manifestNow.AddSeconds(1), manifestNow,
            manifestNow.AddSeconds(1), "ready", "legacy-v1", "legacy:test", [], null, null, null, null, 0, manifestNow);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(legacyManifest, jsonOptions));

        await database.Database.MigrateAsync("20261031013654_AddClipAnalysisDispatch");
        database.ChangeTracker.Clear();
        var initializer = services.GetRequiredService<IamDatabaseInitializer>();
        await initializer.InitializeAsync();

        var upgradedCamera = await database.CameraCatalog.AsNoTracking().SingleAsync();
        var upgradedContext = await database.CameraContexts.AsNoTracking().SingleAsync();
        Assert.Equal(camera.CameraId, upgradedCamera.CameraId);
        Assert.Equal(context.ContextId, upgradedContext.ContextId);
        Assert.True(PublicIdFormat.IsValid(upgradedCamera.PublicId, PublicIdPrefixes.CameraPrefix));
        Assert.True(PublicIdFormat.IsValid(upgradedContext.PublicId, PublicIdPrefixes.ContextPrefix));
        var upgradedProfileId = await database.AccessProfiles.Where(item => item.Id == profileId)
            .Select(item => item.PublicId).SingleAsync();
        Assert.True(PublicIdFormat.IsValid(upgradedProfileId, PublicIdPrefixes.AccessProfilePrefix));
        Assert.True(PublicIdFormat.IsValid(await database.UserAccessProfileAssignments.Where(item => item.Id == assignment.Id)
            .Select(item => item.PublicId).SingleAsync(), PublicIdPrefixes.ProfileAssignmentPrefix));
        Assert.True(PublicIdFormat.IsValid(await database.AccessRequests.Where(item => item.Id == accessRequest.Id)
            .Select(item => item.PublicId).SingleAsync(), PublicIdPrefixes.AccessRequestPrefix));
        Assert.True(PublicIdFormat.IsValid(await database.ClipAnalysisJobs.Where(item => item.Id == analysisJob.Id)
            .Select(item => item.PublicId).SingleAsync(), PublicIdPrefixes.ClipAnalysisJobPrefix));
        Assert.All(await database.Users.Select(item => item.PublicId).ToArrayAsync(), id =>
            Assert.True(PublicIdFormat.IsValid(id, PublicIdPrefixes.UserPrefix)));

        var migratedManifest = JsonSerializer.Deserialize<ClipManifestV1>(await File.ReadAllTextAsync(manifestPath), jsonOptions);
        Assert.NotNull(migratedManifest);
        Assert.True(PublicIdFormat.IsValid(migratedManifest.PublicId, PublicIdPrefixes.ClipPrefix));
        Assert.Equal(clipKey, migratedManifest.ClipId);
        var stableClipPublicId = migratedManifest.PublicId;
        await initializer.InitializeAsync();
        var restartedManifest = JsonSerializer.Deserialize<ClipManifestV1>(await File.ReadAllTextAsync(manifestPath), jsonOptions);
        Assert.Equal(stableClipPublicId, restartedManifest?.PublicId);
    }

    private sealed class ScriptedPublicIdGenerator : IPublicIdGenerator
    {
        private readonly PublicIdGenerator fallback = new();
        private readonly Dictionary<string, Queue<string>> queued = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> generated = new(StringComparer.Ordinal);

        public void Enqueue(string prefix, string value)
        {
            if (!queued.TryGetValue(prefix, out var values)) queued[prefix] = values = new Queue<string>();
            values.Enqueue(value);
        }

        public int Generated(string prefix) => generated.GetValueOrDefault(prefix);

        public string Generate(string prefix)
        {
            generated[prefix] = Generated(prefix) + 1;
            return queued.TryGetValue(prefix, out var values) && values.Count > 0
                ? values.Dequeue()
                : fallback.Generate(prefix);
        }
    }
}
