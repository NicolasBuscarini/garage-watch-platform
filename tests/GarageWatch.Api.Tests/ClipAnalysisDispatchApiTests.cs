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
    public async Task Authorized_manual_request_is_deduplicated_and_never_rejects_even_administrator()
    {
        await using var factory = new ApiFactory();
        var cameraId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        var clip = await PersistReadyClipAsync(factory, cameraId);
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var policy = await admin.PutAsJsonAsync($"/api/v1/cameras/{cameraId}/clip-analysis-dispatch-policy",
            new { expectedVersion = 0, policy = "manual_only" });
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
        var policyBody = await policy.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, policyBody.GetProperty("version").GetInt64());

        var key = Guid.NewGuid();
        using var first = await admin.PostAsJsonAsync($"/api/v1/clips/{clip.ClipId}/analysis-requests",
            new { idempotencyKey = key, analysisVersion = "contract-v1" });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using var retry = await admin.PostAsJsonAsync($"/api/v1/clips/{clip.ClipId}/analysis-requests",
            new { idempotencyKey = key, analysisVersion = "contract-v1" });
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var retried = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(retried.GetProperty("existing").GetBoolean());

        using var never = await admin.PutAsJsonAsync($"/api/v1/cameras/{cameraId}/clip-analysis-dispatch-policy",
            new { expectedVersion = 1, policy = "never" });
        Assert.Equal(HttpStatusCode.OK, never.StatusCode);
        using var denied = await admin.PostAsJsonAsync($"/api/v1/clips/{clip.ClipId}/analysis-requests",
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
        using (var scope = factory.Services.CreateScope())
        {
            var services = scope.ServiceProvider;
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            adminId = (await users.FindByNameAsync("contract-user"))!.Id;
            cameraId = services.GetRequiredService<CaptureOptions>().CameraId;
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "clip-operator", IsActive = true };
            Assert.True((await users.CreateAsync(user, "Clip-Operator-Password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, "User")).Succeeded);
            operatorId = user.Id;
            var profiles = services.GetRequiredService<AccessProfileService>();
            var requestOnly = await profiles.CreateAsync(adminId, "Clip request only", ["clips.analysis.request"],
                CancellationToken.None);
            await profiles.ReplaceAssignmentsAsync(adminId, operatorId,
                [new ProfileAssignmentRequest(requestOnly.CurrentVersionId, [new("garage", cameraId)])],
                CancellationToken.None);
        }
        var clip = await PersistReadyClipAsync(factory, cameraId);
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var policy = await admin.PutAsJsonAsync($"/api/v1/cameras/{cameraId}/clip-analysis-dispatch-policy",
            new { expectedVersion = 0, policy = "manual_only" });
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);

        using var operatorClient = await LoginAsync(factory, "clip-operator", "Clip-Operator-Password-123!");
        operatorClient.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var request = await operatorClient.PostAsJsonAsync($"/api/v1/clips/{clip.ClipId}/analysis-requests",
            new { idempotencyKey = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);
        using var manageDenied = await operatorClient.PutAsJsonAsync(
            $"/api/v1/cameras/{cameraId}/clip-analysis-dispatch-policy", new { expectedVersion = 1, policy = "always" });
        Assert.Equal(HttpStatusCode.Forbidden, manageDenied.StatusCode);
    }

    [Fact]
    public async Task Automatic_dispatch_obeys_policy_and_lease_recovery_preserves_one_logical_job()
    {
        await using var factory = new ApiFactory();
        var cameraId = factory.Services.GetRequiredService<CaptureOptions>().CameraId;
        var clip = await PersistReadyClipAsync(factory, cameraId);
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var policy = await admin.PutAsJsonAsync($"/api/v1/cameras/{cameraId}/clip-analysis-dispatch-policy",
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
        Assert.Equal(lease!.JobId, reclaimed!.JobId);
        Assert.Equal(2, (await database.ClipAnalysisJobs.SingleAsync()).AttemptCount);

        await dispatch.AutoRequestAsync(clip, CancellationToken.None);
        Assert.Equal(1, await database.ClipAnalysisJobs.CountAsync());
    }

    [Fact]
    public async Task Cross_scope_denial_hides_clip_and_manual_only_never_dispatches_automatically()
    {
        await using var factory = new ApiFactory();
        Guid adminId;
        Guid operatorId;
        string cameraId;
        using (var seedScope = factory.Services.CreateScope())
        {
            var services = seedScope.ServiceProvider;
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            adminId = (await users.FindByNameAsync("contract-user"))!.Id;
            cameraId = services.GetRequiredService<CaptureOptions>().CameraId;
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
                [new ProfileAssignmentRequest(requestOnly.CurrentVersionId, [new("garage", "other-camera")])],
                CancellationToken.None);
        }

        var clip = await PersistReadyClipAsync(factory, cameraId);
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var manual = await admin.PutAsJsonAsync($"/api/v1/cameras/{cameraId}/clip-analysis-dispatch-policy",
            new { expectedVersion = 0, policy = "manual_only" });
        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        using var stale = await admin.PutAsJsonAsync($"/api/v1/cameras/{cameraId}/clip-analysis-dispatch-policy",
            new { expectedVersion = 0, policy = "always" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var crossed = await LoginAsync(factory, "crossed-operator", "Crossed-Operator-Password-123!");
        crossed.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var denied = await crossed.PostAsJsonAsync($"/api/v1/clips/{clip.ClipId}/analysis-requests",
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
        var clip = await PersistReadyClipAsync(factory, cameraId);
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var policy = await admin.PutAsJsonAsync($"/api/v1/cameras/{cameraId}/clip-analysis-dispatch-policy",
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

        using var rejected = await admin.PostAsJsonAsync($"/api/v1/clips/{clip.ClipId}/analysis-requests",
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
            "visual_change:test", [], mediaName, hash, null, null, 1, now);
        await File.WriteAllTextAsync(Path.Combine(manifestDirectory, $"{clipId}.json"),
            JsonSerializer.Serialize(clip, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return clip;
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
