using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class AccessRequestsApiTests
{
    [Fact]
    public async Task User_request_is_durable_deduplicated_audited_and_never_grants_access()
    {
        await using var factory = new ApiFactory();
        var userId = await CreateUserAsync(factory, "request-user");
        using var user = await LoginAsync(factory, "request-user", "Request-Password-123!");
        user.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var forbiddenAdminList = await user.GetAsync("/api/v1/admin/access-requests");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenAdminList.StatusCode);
        var key = Guid.NewGuid();

        using var create = await user.PostAsJsonAsync("/api/v1/access-requests", new
        {
            idempotencyKey = key,
            purpose = AccessRequestPurposes.LiveMonitoring
        });
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var requestId = created.GetProperty("id").GetGuid();
        Assert.Equal("pending", created.GetProperty("status").GetString());

        using var duplicate = await user.PostAsJsonAsync("/api/v1/access-requests", new
        {
            idempotencyKey = Guid.NewGuid(),
            purpose = AccessRequestPurposes.CameraAccess
        });
        Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
        var samePending = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(requestId, samePending.GetProperty("id").GetGuid());

        using var conflictingKey = await user.PostAsJsonAsync("/api/v1/access-requests", new
        {
            idempotencyKey = key,
            purpose = AccessRequestPurposes.CameraAccess
        });
        Assert.Equal(HttpStatusCode.Conflict, conflictingKey.StatusCode);

        using var mine = await user.GetAsync("/api/v1/access-requests/mine");
        var ownRequest = await mine.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(requestId, ownRequest.GetProperty("request").GetProperty("id").GetGuid());

        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var list = await admin.GetAsync("/api/v1/admin/access-requests?page=1&pageSize=10");
        list.EnsureSuccessStatusCode();
        var listing = await list.Content.ReadFromJsonAsync<JsonElement>();
        var item = Assert.Single(listing.GetProperty("items").EnumerateArray());
        Assert.Equal(requestId, item.GetProperty("id").GetGuid());
        Assert.Equal("request-user", item.GetProperty("username").GetString());
        Assert.Equal("pending", item.GetProperty("status").GetString());
        Assert.False(item.TryGetProperty("email", out _));

        using var reviewed = await admin.PostAsync($"/api/v1/admin/access-requests/{requestId}/review", null);
        Assert.Equal(HttpStatusCode.NoContent, reviewed.StatusCode);
        using var noLongerPending = await user.GetAsync("/api/v1/access-requests/mine");
        var current = await noLongerPending.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, current.GetProperty("request").ValueKind);

        using var afterReviewList = await admin.GetAsync("/api/v1/admin/access-requests");
        var reviewedItems = await afterReviewList.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("reviewed", Assert.Single(reviewedItems.GetProperty("items").EnumerateArray())
            .GetProperty("status").GetString());

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.Equal(0, await database.CameraAccessGrants.CountAsync(grant => grant.UserId == userId));
        Assert.Equal(1, await database.IamAuditEvents.CountAsync(item => item.SubjectUserId == userId &&
            item.Action == "access.request.created"));
        Assert.Equal(1, await database.IamAuditEvents.CountAsync(item => item.SubjectUserId == userId &&
            item.Action == "access.request.reviewed"));
    }

    [Fact]
    public async Task Access_request_is_authenticated_same_origin_user_only_and_rate_limited()
    {
        await using var factory = new ApiFactory();
        await CreateUserAsync(factory, "limited-user");
        using var anonymous = factory.CreateClient();
        using var anonymousResponse = await anonymous.PostAsJsonAsync("/api/v1/access-requests", new
        {
            idempotencyKey = Guid.NewGuid(),
            purpose = AccessRequestPurposes.LiveMonitoring
        });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        using var user = await LoginAsync(factory, "limited-user", "Request-Password-123!");
        user.DefaultRequestHeaders.Add("Origin", "http://localhost");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var accepted = await user.PostAsJsonAsync("/api/v1/access-requests", new
            {
                idempotencyKey = Guid.NewGuid(),
                purpose = AccessRequestPurposes.LiveMonitoring
            });
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }
        using var limited = await user.PostAsJsonAsync("/api/v1/access-requests", new
        {
            idempotencyKey = Guid.NewGuid(),
            purpose = AccessRequestPurposes.LiveMonitoring
        });
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
    }

    [Fact]
    public async Task Access_request_rejects_cross_origin_cookie_mutation()
    {
        await using var factory = new ApiFactory();
        await CreateUserAsync(factory, "origin-user");
        using var user = await LoginAsync(factory, "origin-user", "Request-Password-123!");
        user.DefaultRequestHeaders.Add("Origin", "https://attacker.invalid");
        using var response = await user.PostAsJsonAsync("/api/v1/access-requests", new
        {
            idempotencyKey = Guid.NewGuid(),
            purpose = AccessRequestPurposes.LiveMonitoring
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<IamDbContext>().AccessRequests.CountAsync());
    }

    [Fact]
    public async Task Pending_request_survives_api_restart_and_same_user_can_read_only_its_request()
    {
        var path = Path.Combine(Path.GetTempPath(), "garagewatch-tests", $"{Guid.NewGuid():N}.db");
        Guid requestId;
        await using (var firstFactory = new ApiFactory(databasePath: path))
        {
            await CreateUserAsync(firstFactory, "durable-user");
            using var user = await LoginAsync(firstFactory, "durable-user", "Request-Password-123!");
            user.DefaultRequestHeaders.Add("Origin", "http://localhost");
            using var response = await user.PostAsJsonAsync("/api/v1/access-requests", new
            {
                idempotencyKey = Guid.NewGuid(),
                purpose = AccessRequestPurposes.LiveMonitoring
            });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            requestId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }

        await using var restartedFactory = new ApiFactory(databasePath: path);
        using var restartedUser = await LoginAsync(restartedFactory, "durable-user", "Request-Password-123!");
        using var persisted = await restartedUser.GetAsync("/api/v1/access-requests/mine");
        persisted.EnsureSuccessStatusCode();
        var payload = await persisted.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(requestId, payload.GetProperty("request").GetProperty("id").GetGuid());
    }

    private static async Task<Guid> CreateUserAsync(ApiFactory factory, string username)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = username, IsActive = true };
        Assert.True((await users.CreateAsync(user, "Request-Password-123!")).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, "User")).Succeeded);
        return user.Id;
    }

    private static async Task<HttpClient> LoginAsync(ApiFactory factory, string username, string password)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie",
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        return client;
    }
}
