using System.Net;
using System.Net.Http.Json;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class RegistrationTests
{
    private const string ValidPassword = "Register-Password-123!";

    [Fact]
    public async Task Registration_creates_active_User_without_email_or_camera_and_user_cannot_open_monitoring()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        SetSameOrigin(client);

        using var registered = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = "new-local-user", password = ValidPassword, confirmPassword = ValidPassword });
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        Assert.False(registered.Headers.Contains("Set-Cookie"));

        Guid userId;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var user = await users.FindByNameAsync("new-local-user");
            Assert.NotNull(user);
            userId = user.Id;
            Assert.True(user.IsActive);
            Assert.False(user.EmailConfirmed);
            Assert.Null(user.Email);
            Assert.NotEqual(ValidPassword, user.PasswordHash);
            Assert.True(await users.IsInRoleAsync(user, "User"));
            Assert.False(await users.IsInRoleAsync(user, "Viewer"));
            Assert.False(await users.IsInRoleAsync(user, "Administrator"));
            Assert.Equal(0, await database.CameraAccessGrants.CountAsync(grant => grant.UserId == userId));
            var audit = await database.IamAuditEvents.SingleAsync(entry => entry.SubjectUserId == userId);
            Assert.Equal("user.registered", audit.Action);
            Assert.Null(audit.ActorUserId);
        }

        using var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "new-local-user", password = ValidPassword });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", Assert.Single(login.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);

        var me = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/auth/me");
        Assert.Equal("User", me.GetProperty("role").GetString());
        Assert.False(me.GetProperty("canMonitor").GetBoolean());
        using var cameras = await client.GetAsync("/api/v1/cameras");
        Assert.Equal(HttpStatusCode.OK, cameras.StatusCode);
        using var cameraList = await cameras.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
        Assert.Empty(cameraList!.RootElement.EnumerateArray());
        using var readiness = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.Forbidden, readiness.StatusCode);
    }

    [Fact]
    public async Task Duplicate_username_and_new_registration_have_identical_responses()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        SetSameOrigin(client);
        var request = new { username = "unique-local-user", password = ValidPassword, confirmPassword = ValidPassword };

        using var first = await client.PostAsJsonAsync("/api/v1/auth/register", request);
        using var duplicate = await client.PostAsJsonAsync("/api/v1/auth/register", request);
        Assert.Equal(first.StatusCode, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await duplicate.Content.ReadAsStringAsync());
        Assert.False(first.Headers.Contains("Set-Cookie"));
        Assert.False(duplicate.Headers.Contains("Set-Cookie"));

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.Equal(2, await users.Users.CountAsync());
        Assert.Equal(1, await database.IamAuditEvents.CountAsync(entry => entry.Action == "user.registered"));
    }

    [Fact]
    public async Task Login_uses_identical_unauthorized_response_for_unknown_and_wrong_password_accounts()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var unknown = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "unknown-local-user", password = ValidPassword });
        using var wrongPassword = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = ValidPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(unknown.StatusCode, wrongPassword.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await wrongPassword.Content.ReadAsStringAsync());
        Assert.False(unknown.Headers.Contains("Set-Cookie"));
        Assert.False(wrongPassword.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Registration_rejects_missing_cross_origin_and_host_spoofed_origins()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var missingOrigin = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = "missing-origin", password = ValidPassword, confirmPassword = ValidPassword });
        Assert.Equal(HttpStatusCode.Forbidden, missingOrigin.StatusCode);

        client.DefaultRequestHeaders.Add("Origin", "https://attacker.invalid");
        using var crossOrigin = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = "cross-origin", password = ValidPassword, confirmPassword = ValidPassword });
        Assert.Equal(HttpStatusCode.Forbidden, crossOrigin.StatusCode);

        client.DefaultRequestHeaders.Remove("Origin");
        using var hostSpoofRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = JsonContent.Create(new
            {
                username = "spoofed-host",
                password = ValidPassword,
                confirmPassword = ValidPassword
            })
        };
        hostSpoofRequest.Headers.Host = "attacker.invalid";
        hostSpoofRequest.Headers.Add("Origin", "http://attacker.invalid");
        using var hostSpoof = await client.SendAsync(hostSpoofRequest);
        Assert.Equal(HttpStatusCode.Forbidden, hostSpoof.StatusCode);

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Equal(1, await users.Users.CountAsync());
    }

    [Fact]
    public async Task Registration_rejects_invalid_input_without_creating_account()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        SetSameOrigin(client);

        using var invalid = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = "bad name", password = "short", confirmPassword = "different" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Equal(1, await users.Users.CountAsync());
    }

    [Fact]
    public async Task Registration_accepts_six_numeric_characters_and_rejects_five()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        SetSameOrigin(client);

        using var tooShort = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = "five-digits", password = "12345", confirmPassword = "12345" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        using var accepted = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = "six-digits", password = "123456", confirmPassword = "123456" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        using var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "six-digits", password = "123456" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
    }

    [Fact]
    public async Task Registration_rate_limit_rejects_requests_after_three_attempts_per_minute()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        SetSameOrigin(client);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var accepted = await client.PostAsJsonAsync("/api/v1/auth/register", new
            {
                username = $"rate-user-{attempt}",
                password = ValidPassword,
                confirmPassword = ValidPassword
            });
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using var rejected = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username = "rate-user-extra",
            password = ValidPassword,
            confirmPassword = ValidPassword
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    private static void SetSameOrigin(HttpClient client) =>
        client.DefaultRequestHeaders.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
}
