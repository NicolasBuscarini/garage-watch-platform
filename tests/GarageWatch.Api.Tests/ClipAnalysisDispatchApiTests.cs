using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class ClipAnalysisDispatchApiTests
{
    [Fact]
    public async Task Clip_trigger_configuration_is_per_camera_persisted_versioned_and_applies_immediately()
    {
        await using var factory = new ApiFactory();
        var internalCameraId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        var publicCameraId = await GetCameraPublicIdAsync(factory);
        using (var anonymous = factory.CreateClient())
        {
            using var denied = await anonymous.GetAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "trigger-viewer", IsActive = true };
            Assert.True((await users.CreateAsync(user, "Trigger-Viewer-Password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "User")).Succeeded);
        }
        using (var nonAdmin = await LoginAsync(factory, "trigger-viewer", "Trigger-Viewer-Password-123!"))
        {
            nonAdmin.DefaultRequestHeaders.Add("Origin", "http://localhost");
            using var denied = await nonAdmin.PutAsJsonAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger",
                new { expectedVersion = 0, enabled = true });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        using (var missingOrigin = await LoginAsync(factory, "contract-user", "Contract-Password-123!"))
        {
            using var denied = await missingOrigin.PutAsJsonAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger",
                new { expectedVersion = 0, enabled = true });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            missingOrigin.DefaultRequestHeaders.Add("Origin", "https://attacker.invalid");
            using var crossOrigin = await missingOrigin.PutAsJsonAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger",
                new { expectedVersion = 0, enabled = true });
            Assert.Equal(HttpStatusCode.Forbidden, crossOrigin.StatusCode);
        }
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var initial = await admin.GetAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        var initialBody = await initial.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(initialBody.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, initialBody.GetProperty("version").GetInt64());

        using var enabled = await admin.PutAsJsonAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger",
            new { expectedVersion = 0, enabled = true });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var enabledBody = await enabled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(enabledBody.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, enabledBody.GetProperty("version").GetInt64());
        using (var scope = factory.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<CameraClipTriggerConfigurationService>()
                .IsEnabledAsync(internalCameraId, CancellationToken.None));

        using var stale = await admin.PutAsJsonAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger",
            new { expectedVersion = 0, enabled = false });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var disabled = await admin.PutAsJsonAsync($"/api/v1/admin/cameras/{publicCameraId}/clip-trigger",
            new { expectedVersion = 1, enabled = false });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        using (var scope = factory.Services.CreateScope())
            Assert.False(await scope.ServiceProvider.GetRequiredService<CameraClipTriggerConfigurationService>()
                .IsEnabledAsync(internalCameraId, CancellationToken.None));
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            Assert.Equal(2, await database.IamAuditEvents.CountAsync(item =>
                item.Action == "clip.trigger.configuration.updated"));
        }
    }

    [Fact]
    public async Task Clip_trigger_accepts_exact_https_origin_through_tls_terminating_tunnel_without_forwarded_headers()
    {
        await using var factory = new ApiFactory();
        var cameraPublicId = await GetCameraPublicIdAsync(factory);
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");

        using var wrongHost = await SendTunnelTriggerUpdateAsync(admin, cameraPublicId,
            "https://attacker.example", "home-ia.nklas.loan");
        Assert.Equal(HttpStatusCode.Forbidden, wrongHost.StatusCode);
        using var insecureOrigin = await SendTunnelTriggerUpdateAsync(admin, cameraPublicId,
            "http://home-ia.nklas.loan", "home-ia.nklas.loan");
        Assert.Equal(HttpStatusCode.Forbidden, insecureOrigin.StatusCode);
        using var wrongPort = await SendTunnelTriggerUpdateAsync(admin, cameraPublicId,
            "https://home-ia.nklas.loan:8443", "home-ia.nklas.loan");
        Assert.Equal(HttpStatusCode.Forbidden, wrongPort.StatusCode);

        using var accepted = await SendTunnelTriggerUpdateAsync(admin, cameraPublicId,
            "https://home-ia.nklas.loan", "home-ia.nklas.loan");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, body.GetProperty("version").GetInt64());
    }

    private static async Task<HttpResponseMessage> SendTunnelTriggerUpdateAsync(HttpClient client, string cameraId,
        string origin, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/cameras/{cameraId}/clip-trigger")
        {
            Content = JsonContent.Create(new { expectedVersion = 0, enabled = true })
        };
        request.Headers.Host = host;
        request.Headers.TryAddWithoutValidation("Origin", origin);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Authorized_manual_request_is_deduplicated_and_never_rejects_even_administrator()
    {
        await using var factory = new ApiFactory();
        var cameraId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        var cameraPublicId = await GetCameraPublicIdAsync(factory);
        var clip = await PersistReadyClipAsync(factory, cameraId);
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var legacyCameraPolicy = await admin.PutAsJsonAsync($"/api/v1/cameras/{cameraId}/clip-analysis-dispatch-policy",
            new { expectedVersion = 0, policy = "manual_only" });
        Assert.Equal(HttpStatusCode.NotFound, legacyCameraPolicy.StatusCode);

        using var policy = await admin.PutAsJsonAsync($"/api/v1/cameras/{cameraPublicId}/clip-analysis-dispatch-policy",
            new { expectedVersion = 0, policy = "manual_only" });
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
        var policyBody = await policy.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, policyBody.GetProperty("version").GetInt64());
        using var readPolicy = await admin.GetAsync($"/api/v1/cameras/{cameraPublicId}/clip-analysis-dispatch-policy");
        Assert.Equal(HttpStatusCode.OK, readPolicy.StatusCode);
        var readPolicyBody = await readPolicy.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("manual_only", readPolicyBody.GetProperty("policy").GetString());
        Assert.True(readPolicyBody.GetProperty("canRequest").GetBoolean());
        Assert.True(readPolicyBody.GetProperty("canManage").GetBoolean());

        var key = Guid.NewGuid();
        using var first = await admin.PostAsJsonAsync($"/api/v1/clips/{clip.PublicId}/analysis-requests",
            new { idempotencyKey = key, analysisVersion = "contract-v1" });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using var retry = await admin.PostAsJsonAsync($"/api/v1/clips/{clip.PublicId}/analysis-requests",
            new { idempotencyKey = key, analysisVersion = "contract-v1" });
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var retried = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(retried.GetProperty("existing").GetBoolean());

        using var never = await admin.PutAsJsonAsync($"/api/v1/cameras/{cameraPublicId}/clip-analysis-dispatch-policy",
            new { expectedVersion = 1, policy = "never" });
        Assert.Equal(HttpStatusCode.OK, never.StatusCode);
        using var denied = await admin.PostAsJsonAsync($"/api/v1/clips/{clip.PublicId}/analysis-requests",
            new { idempotencyKey = Guid.NewGuid(), analysisVersion = "contract-v2" });
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.Equal(1, await database.ClipAnalysisJobs.CountAsync());
        Assert.Contains(await database.IamAuditEvents.Select(item => item.Action).ToArrayAsync(),
            action => action == "clip.analysis.request.denied");
    }

    [Fact]
    public async Task Policies_and_requests_require_the_matching_delegated_capability_and_scope()
    {
        await using var factory = new ApiFactory();
        Guid adminId;
        Guid operatorId;
        string cameraId;
        string cameraPublicId;
        using (var scope = factory.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            adminId = (await users.FindByNameAsync("contract-user"))!.Id;
            cameraId = services.GetRequiredService<CaptureOptions>().CameraId;
            cameraPublicId = await GetCameraPublicIdAsync(factory);
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "clip-operator", IsActive = true };
            Assert.True((await users.CreateAsync(user, "Clip-Operator-Password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "User")).Succeeded);
            operatorId = user.Id;
            var profiles = services.GetRequiredService<AccessProfileService>();
            var requestOnly = await profiles.CreateAsync(adminId, "Clip request only", ["clips.read", "clips.analysis.request"],
                CancellationToken.None);
            await profiles.ReplaceAssignmentsAsync(adminId, operatorId,
                [new ProfileAssignmentRequest(await GetVersionIdAsync(services, requestOnly.CurrentVersionPublicId), [new("garage", cameraId)])],
                CancellationToken.None);
        }
        var clip = await PersistReadyClipAsync(factory, cameraId);
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var policy = await admin.PutAsJsonAsync($"/api/v1/cameras/{cameraPublicId}/clip-analysis-dispatch-policy",
            new { expectedVersion = 0, policy = "manual_only" });
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);

        using var operatorClient = await LoginAsync(factory, "clip-operator", "Clip-Operator-Password-123!");
        operatorClient.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var readPolicy = await operatorClient.GetAsync(
            $"/api/v1/cameras/{cameraPublicId}/clip-analysis-dispatch-policy");
        Assert.Equal(HttpStatusCode.OK, readPolicy.StatusCode);
        var policyView = await readPolicy.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(policyView.GetProperty("canRequest").GetBoolean());
        Assert.False(policyView.GetProperty("canManage").GetBoolean());
        using var request = await operatorClient.PostAsJsonAsync($"/api/v1/clips/{clip.PublicId}/analysis-requests",
            new { idempotencyKey = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        using var manageDenied = await operatorClient.PutAsJsonAsync(
            $"/api/v1/cameras/{cameraPublicId}/clip-analysis-dispatch-policy", new { expectedVersion = 1, policy = "always" });
        Assert.Equal(HttpStatusCode.Forbidden, manageDenied.StatusCode);
    }

    [Fact]
    public async Task Automatic_dispatch_obeys_policy_and_lease_recovery_preserves_one_logical_job()
    {
        await using var factory = new ApiFactory();
        var cameraId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        var cameraPublicId = await GetCameraPublicIdAsync(factory);
        var clip = await PersistReadyClipAsync(factory, cameraId);
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var policy = await admin.PutAsJsonAsync($"/api/v1/cameras/{cameraPublicId}/clip-analysis-dispatch-policy",
            new { expectedVersion = 0, policy = "always" });
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<ClipAnalysisDispatchService>();
        await dispatch.AutoRequestAsync(clip, CancellationToken.None);
        var lease = await dispatch.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(lease);
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        var job = await database.ClipAnalysisJobs.SingleAsync();
        job.LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        await database.SaveChangesAsync();

        var reclaimed = await dispatch.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(reclaimed);
        Assert.Equal(lease!.PublicId, reclaimed!.PublicId);
        Assert.Equal(2, (await database.ClipAnalysisJobs.SingleAsync()).AttemptCount);

        await dispatch.AutoRequestAsync(clip, CancellationToken.None);
        Assert.Equal(1, await database.ClipAnalysisJobs.CountAsync());
    }

    [Fact]
    public async Task Known_person_registry_requires_admin_and_authorization_and_removal_erases_roster_fields()
    {
        await using var factory = new ApiFactory();
        using (var anonymous = factory.CreateClient())
        {
            using var denied = await anonymous.GetAsync("/api/v1/admin/known-people");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "known-person-user", IsActive = true };
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.CreateAsync(user, "Known-Person-Password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "User")).Succeeded);
        }
        using (var nonAdmin = await LoginAsync(factory, "known-person-user", "Known-Person-Password-123!"))
        {
            nonAdmin.DefaultRequestHeaders.Add("Origin", "http://localhost");
            using var denied = await nonAdmin.GetAsync("/api/v1/admin/known-people");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var deniedCreate = await nonAdmin.PostAsJsonAsync("/api/v1/admin/known-people",
                new { displayName = "Unauthorized Person", purpose = "test", authorizationConfirmed = true });
            Assert.Equal(HttpStatusCode.Forbidden, deniedCreate.StatusCode);
        }

        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        using var missingOrigin = await admin.PostAsJsonAsync("/api/v1/admin/known-people",
            new { displayName = "Origin Test Person", purpose = "test", authorizationConfirmed = true });
        Assert.Equal(HttpStatusCode.Forbidden, missingOrigin.StatusCode);
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var missingAuthorization = await admin.PostAsJsonAsync("/api/v1/admin/known-people",
            new { displayName = "Authorized Test Person", purpose = "Household recognition", authorizationConfirmed = false });
        Assert.Equal(HttpStatusCode.BadRequest, missingAuthorization.StatusCode);

        using var created = await admin.PostAsJsonAsync("/api/v1/admin/known-people",
            new { displayName = "Authorized Test Person", purpose = "Household recognition", authorizationConfirmed = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var person = await created.Content.ReadFromJsonAsync<JsonElement>();
        var personPublicId = person.GetProperty("publicId").GetString()!;
        Assert.Matches("^PER[0-9]{12}$", personPublicId);
        Assert.Equal("Household recognition", person.GetProperty("purpose").GetString());
        Assert.NotEqual(default, person.GetProperty("authorizationRecordedAtUtc").GetDateTimeOffset());

        using var listed = await admin.GetAsync("/api/v1/admin/known-people");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var list = await listed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(personPublicId, list.GetProperty("items")[0].GetProperty("publicId").GetString());

        using var removed = await admin.DeleteAsync($"/api/v1/admin/known-people/{personPublicId}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using var repeatedRemoval = await admin.DeleteAsync($"/api/v1/admin/known-people/{personPublicId}");
        Assert.Equal(HttpStatusCode.NoContent, repeatedRemoval.StatusCode);
        using var listedAfterRemoval = await admin.GetAsync("/api/v1/admin/known-people");
        var emptyList = await listedAfterRemoval.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(emptyList.GetProperty("items").EnumerateArray());
        using (var dataScope = factory.Services.CreateScope())
        {
            var database = dataScope.ServiceProvider.GetRequiredService<IamDbContext>();
            var tombstone = await database.KnownPeople.SingleAsync(item => item.PublicId == personPublicId);
            Assert.Null(tombstone.DisplayName);
            Assert.Null(tombstone.Purpose);
            Assert.Null(tombstone.AuthorizationRecordedAtUtc);
            Assert.NotNull(tombstone.DeletedAtUtc);
            Assert.Contains(await database.IamAuditEvents.Where(item => item.Action == "known_person.removed")
                .Select(item => item.ResourceId).ToArrayAsync(), id => id == personPublicId);
            var creationAudit = await database.IamAuditEvents.SingleAsync(item => item.Action == "known_person.created");
            Assert.Equal(personPublicId, creationAudit.ResourceId);
            Assert.DoesNotContain("Authorized Test Person", creationAudit.ResourceId, StringComparison.Ordinal);
        }

        var databasePath = factory.DatabasePath;
        await factory.DisposeAsync();
        await using var restartedFactory = new ApiFactory(databasePath: databasePath);
        using var restartedAdmin = await LoginAsync(restartedFactory, "contract-user", "Contract-Password-123!");
        using var afterRestart = await restartedAdmin.GetAsync("/api/v1/admin/known-people");
        var afterRestartList = await afterRestart.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(afterRestartList.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Cross_scope_denial_hides_clip_and_manual_only_never_dispatches_automatically()
    {
        await using var factory = new ApiFactory();
        Guid adminId;
        Guid operatorId;
        string cameraId;
        string cameraPublicId;
        using (var seedScope = factory.Services.CreateScope())
        {
            var services = seedScope.ServiceProvider;
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            adminId = (await users.FindByNameAsync("contract-user"))!.Id;
            cameraId = services.GetRequiredService<CaptureOptions>().CameraId;
            cameraPublicId = await GetCameraPublicIdAsync(factory);
            var seedDatabase = services.GetRequiredService<IamDbContext>();
            seedDatabase.CameraCatalog.Add(new CameraCatalogEntry
            {
                CameraId = "other-camera",
                ContextId = "garage",
                Name = "Other",
                IsEnabled = true,
                Version = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await seedDatabase.SaveChangesAsync();
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "crossed-operator", IsActive = true };
            Assert.True((await users.CreateAsync(user, "Crossed-Operator-Password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "User")).Succeeded);
            operatorId = user.Id;
            var profiles = services.GetRequiredService<AccessProfileService>();
            var requestOnly = await profiles.CreateAsync(adminId, "Crossed request only",
                ["clips.analysis.request"], CancellationToken.None);
            await profiles.ReplaceAssignmentsAsync(adminId, operatorId,
                [new ProfileAssignmentRequest(await GetVersionIdAsync(services, requestOnly.CurrentVersionPublicId), [new("garage", "other-camera")])],
                CancellationToken.None);
        }

        var clip = await PersistReadyClipAsync(factory, cameraId);
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var manual = await admin.PutAsJsonAsync($"/api/v1/cameras/{cameraPublicId}/clip-analysis-dispatch-policy",
            new { expectedVersion = 0, policy = "manual_only" });
        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        using var stale = await admin.PutAsJsonAsync($"/api/v1/cameras/{cameraPublicId}/clip-analysis-dispatch-policy",
            new { expectedVersion = 0, policy = "always" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var crossed = await LoginAsync(factory, "crossed-operator", "Crossed-Operator-Password-123!");
        crossed.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var denied = await crossed.PostAsJsonAsync($"/api/v1/clips/{clip.PublicId}/analysis-requests",
            new { idempotencyKey = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        using var scope = factory.Services.CreateScope();
        var dispatch = scope.ServiceProvider.GetRequiredService<ClipAnalysisDispatchService>();
        await dispatch.AutoRequestAsync(clip, CancellationToken.None);
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.Empty(await database.ClipAnalysisJobs.ToArrayAsync());
        Assert.Contains(await database.IamAuditEvents.Select(item => item.Action).ToArrayAsync(),
            action => action == "clip.analysis.request.denied");
    }

    [Fact]
    public async Task Bounded_backlog_rejects_manual_request_without_creating_another_job()
    {
        await using var factory = new ApiFactory();
        var cameraId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        var cameraPublicId = await GetCameraPublicIdAsync(factory);
        var clip = await PersistReadyClipAsync(factory, cameraId);
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var policy = await admin.PutAsJsonAsync($"/api/v1/cameras/{cameraPublicId}/clip-analysis-dispatch-policy",
            new { expectedVersion = 0, policy = "manual_only" });
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);

        using (var seedScope = factory.Services.CreateScope())
        {
            var database = seedScope.ServiceProvider.GetRequiredService<IamDbContext>();
            for (var index = 0; index < 8; index++)
                database.ClipAnalysisJobs.Add(new ClipAnalysisJob
                {
                    Id = Guid.NewGuid(),
                    ClipId = $"queued{index}",
                    ContextId = "garage",
                    CameraId = cameraId,
                    IdempotencyKey = Guid.NewGuid(),
                    AnalysisVersion = "analysis-v1",
                    State = "analysis_pending",
                    RequestKind = "automatic",
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
            await database.SaveChangesAsync();
        }

        using var rejected = await admin.PostAsJsonAsync($"/api/v1/clips/{clip.PublicId}/analysis-requests",
            new { idempotencyKey = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        using var checkScope = factory.Services.CreateScope();
        Assert.Equal(8, await checkScope.ServiceProvider.GetRequiredService<IamDbContext>().ClipAnalysisJobs.CountAsync());
    }

    private static async Task<ClipManifestV1> PersistReadyClipAsync(ApiFactory factory, string cameraId)
    {
        var capture = factory.Services.GetRequiredService<CaptureOptions>();
        var clipId = Guid.NewGuid().ToString("N");
        var mediaDirectory = Path.Combine(capture.SpoolDirectory, "clips", "media");
        var manifestDirectory = Path.Combine(capture.SpoolDirectory, "clips", "manifests");
        Directory.CreateDirectory(mediaDirectory);
        Directory.CreateDirectory(manifestDirectory);
        var mediaName = $"{clipId}.ts";
        var media = Path.Combine(mediaDirectory, mediaName);
        await File.WriteAllBytesAsync(media, [0x47, 0x01, 0x02]);
        await using var mediaStream = File.OpenRead(media);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(mediaStream)).ToLowerInvariant();
        var now = DateTimeOffset.UtcNow;
        var clip = new ClipManifestV1("1.0", clipId, cameraId, now, now, now, now, "ready", "trigger-v1",
            "visual_change:test", [], mediaName, hash, null, null, 1, now,
            PublicId: new PublicIdGenerator().Generate(PublicIdPrefixes.ClipPrefix));
        await File.WriteAllTextAsync(Path.Combine(manifestDirectory, $"{clipId}.json"),
            JsonSerializer.Serialize(clip, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return clip;
    }

    private static async Task<Guid> GetVersionIdAsync(IServiceProvider services, string publicId) =>
        await services.GetRequiredService<IamDbContext>().AccessProfileVersions
            .Where(item => item.PublicId == publicId).Select(item => item.Id).SingleAsync();

    private static async Task<string> GetCameraPublicIdAsync(ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var internalId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        return (await scope.ServiceProvider.GetRequiredService<IamDbContext>().CameraCatalog
            .Where(item => item.CameraId == internalId).Select(item => item.PublicId).SingleAsync())!;
    }

    private static async Task<HttpClient> LoginAsync(ApiFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        return client;
    }
}
