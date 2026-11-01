using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GarageWatch.Application.Analysis;
using GarageWatch.Application.Biometrics;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class BiometricEnrollmentApiTests
{
    private const string Password = "Biometric-Password-123!";
    private static readonly string TestKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

    [Fact]
    public async Task Owner_enrolls_own_biometrics_and_store_only_ciphertext_with_one_year_expiry()
    {
        await using var factory = ConfiguredFactory();
        using var client = factory.CreateClient();
        await RegisterAsync(client, "bio-owner");
        await LoginAsync(client, "bio-owner");

        using var created = await client.PostAsJsonAsync("/api/v1/biometrics/enrollments", EnrollmentBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var text = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain("values", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("embedding", text, StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(text);
        var enrollment = body.RootElement;
        var publicId = enrollment.GetProperty("publicId").GetString()!;
        Assert.Matches("^BIO[0-9]{12}$", publicId);
        Assert.Equal("active", enrollment.GetProperty("status").GetString());
        Assert.Equal("test-face-model", enrollment.GetProperty("modelId").GetString());
        Assert.Equal(3, enrollment.GetProperty("dimensions").GetInt32());
        var enrolledAt = enrollment.GetProperty("enrolledAtUtc").GetDateTimeOffset();
        var expiresAt = enrollment.GetProperty("expiresAtUtc").GetDateTimeOffset();
        Assert.Equal(BiometricPolicy.RetentionDays, (expiresAt - enrolledAt).TotalDays, 3);

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var stored = await database.BiometricEnrollments.SingleAsync();
            Assert.Equal(publicId, stored.PublicId);
            Assert.NotEmpty(stored.EmbeddingCiphertext);
            Assert.Equal(12, stored.EmbeddingNonce.Length);
            Assert.Equal(16, stored.EmbeddingTag.Length);
            Assert.Equal("test-1", stored.KeyVersion);
            Assert.Equal(enrolledAt, stored.EnrolledAtUtc);
            Assert.Equal(expiresAt, stored.ExpiresAtUtc);
        }

        using var listed = await client.GetAsync("/api/v1/biometrics/enrollments");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        using var list = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var single = Assert.Single(list.RootElement.EnumerateArray());
        Assert.Equal(publicId, single.GetProperty("publicId").GetString());

        // Re-enrollment replaces the previous active row instead of keeping two usable embeddings.
        using var replacement = await client.PostAsJsonAsync("/api/v1/biometrics/enrollments", EnrollmentBody());
        Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        using var afterReplacement = JsonDocument.Parse(await replacement.Content.ReadAsStringAsync());
        Assert.NotEqual(publicId, afterReplacement.RootElement.GetProperty("publicId").GetString());
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var rows = await database.BiometricEnrollments.OrderBy(item => item.CreatedAtUtc).ToArrayAsync();
            Assert.Equal(2, rows.Length);
            Assert.Equal(BiometricEnrollment.RevokedStatus, rows[0].Status);
            Assert.Equal("replaced", rows[0].RevokedReason);
            Assert.Empty(rows[0].EmbeddingCiphertext);
            Assert.Equal(BiometricEnrollment.ActiveStatus, rows[1].Status);
        }
    }

    [Fact]
    public async Task Only_owner_or_privileged_manager_can_enroll_and_revoke_for_another_account()
    {
        await using var factory = ConfiguredFactory();
        using var ownerClient = factory.CreateClient();
        await RegisterAsync(ownerClient, "bio-owner");
        await LoginAsync(ownerClient, "bio-owner");
        using var otherClient = factory.CreateClient();
        await RegisterAsync(otherClient, "bio-other");
        await LoginAsync(otherClient, "bio-other");
        var ownerPublicId = await GetUserPublicIdAsync(factory, "bio-owner");

        using var created = await otherClient.PostAsJsonAsync("/api/v1/biometrics/enrollments", EnrollmentBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var otherEnrollmentId = createdBody.RootElement.GetProperty("publicId").GetString()!;

        using var deniedEnroll = await otherClient.PostAsJsonAsync("/api/v1/biometrics/enrollments",
            EnrollmentBody() with { UserPublicId = ownerPublicId });
        Assert.Equal(HttpStatusCode.Forbidden, deniedEnroll.StatusCode);
        using var ownRevoke = await otherClient.DeleteAsync(
            $"/api/v1/biometrics/enrollments/{otherEnrollmentId}");
        Assert.Equal(HttpStatusCode.NoContent, ownRevoke.StatusCode);

        using var admin = factory.CreateClient();
        await LoginAsync(admin, "contract-user", "Contract-Password-123!");
        using var adminEnroll = await admin.PostAsJsonAsync("/api/v1/biometrics/enrollments",
            EnrollmentBody() with { UserPublicId = ownerPublicId });
        Assert.Equal(HttpStatusCode.Created, adminEnroll.StatusCode);
        using var adminEnrollBody = JsonDocument.Parse(await adminEnroll.Content.ReadAsStringAsync());
        var ownerEnrollmentId = adminEnrollBody.RootElement.GetProperty("publicId").GetString()!;

        using var ownerList = await ownerClient.GetAsync("/api/v1/biometrics/enrollments");
        Assert.Equal(HttpStatusCode.OK, ownerList.StatusCode);
        using var ownerListBody = JsonDocument.Parse(await ownerList.Content.ReadAsStringAsync());
        var ownerItem = Assert.Single(ownerListBody.RootElement.EnumerateArray());
        Assert.Equal(ownerEnrollmentId, ownerItem.GetProperty("publicId").GetString());

        using var adminRevoke = await admin.DeleteAsync($"/api/v1/biometrics/enrollments/{ownerEnrollmentId}");
        Assert.Equal(HttpStatusCode.NoContent, adminRevoke.StatusCode);
        using var afterRevoke = await ownerClient.GetAsync("/api/v1/biometrics/enrollments");
        using var afterRevokeBody = JsonDocument.Parse(await afterRevoke.Content.ReadAsStringAsync());
        var revoked = Assert.Single(afterRevokeBody.RootElement.EnumerateArray());
        Assert.Equal("revoked", revoked.GetProperty("status").GetString());
        Assert.Equal("administrator_request", revoked.GetProperty("revokedReason").GetString());

        using var otherRevoke = await otherClient.DeleteAsync(
            $"/api/v1/biometrics/enrollments/{ownerEnrollmentId}");
        Assert.Equal(HttpStatusCode.Forbidden, otherRevoke.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            Assert.All(await database.BiometricEnrollments.ToArrayAsync(),
                row => Assert.Empty(row.EmbeddingCiphertext));
        }
    }

    [Fact]
    public async Task Missing_extractor_or_encryption_key_fails_closed_without_persisting()
    {
        await using var withoutExtractor = ConfiguredFactory(withExtractor: false);
        using var extractorClient = withoutExtractor.CreateClient();
        await RegisterAsync(extractorClient, "bio-no-extractor");
        await LoginAsync(extractorClient, "bio-no-extractor");
        using var unavailable = await extractorClient.PostAsJsonAsync("/api/v1/biometrics/enrollments", EnrollmentBody());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Contains("face_extractor_not_configured", await unavailable.Content.ReadAsStringAsync());
        using (var scope = withoutExtractor.Services.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<IamDbContext>().BiometricEnrollments.CountAsync());

        await using var withoutKey = ConfiguredFactory(withKey: false);
        using var keyClient = withoutKey.CreateClient();
        await RegisterAsync(keyClient, "bio-no-key");
        await LoginAsync(keyClient, "bio-no-key");
        using var noKey = await keyClient.PostAsJsonAsync("/api/v1/biometrics/enrollments", EnrollmentBody());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, noKey.StatusCode);
        Assert.Contains("biometric_protection_not_configured", await noKey.Content.ReadAsStringAsync());
        using (var scope = withoutKey.Services.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<IamDbContext>().BiometricEnrollments.CountAsync());
    }

    [Fact]
    public async Task Expired_enrollment_is_erased_by_durable_retention()
    {
        await using var factory = ConfiguredFactory();
        using var client = factory.CreateClient();
        await RegisterAsync(client, "bio-expiring");
        await LoginAsync(client, "bio-expiring");
        using var created = await client.PostAsJsonAsync("/api/v1/biometrics/enrollments", EnrollmentBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var stored = await database.BiometricEnrollments.SingleAsync();
            stored.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            await database.SaveChangesAsync();
        }

        using var listed = await client.GetAsync("/api/v1/biometrics/enrollments");
        using var body = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var expired = Assert.Single(body.RootElement.EnumerateArray());
        Assert.Equal("expired", expired.GetProperty("status").GetString());
        Assert.Equal("expired", expired.GetProperty("revokedReason").GetString());
        using (var scope = factory.Services.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
            var stored = await database.BiometricEnrollments.SingleAsync();
            Assert.Empty(stored.EmbeddingCiphertext);
            Assert.Empty(stored.EmbeddingNonce);
            Assert.Empty(stored.EmbeddingTag);
            Assert.Contains(await database.IamAuditEvents.ToArrayAsync(),
                entry => entry.Action == "biometric.enrollment_expired");
        }
    }

    [Fact]
    public async Task Invalid_image_or_missing_origin_is_rejected()
    {
        await using var factory = ConfiguredFactory();
        using var client = factory.CreateClient();
        await RegisterAsync(client, "bio-invalid");
        await LoginAsync(client, "bio-invalid");

        using var withoutOrigin = factory.CreateClient();
        await LoginAsync(withoutOrigin, "bio-invalid");
        withoutOrigin.DefaultRequestHeaders.Remove("Origin");
        using var forbidden = await withoutOrigin.PostAsJsonAsync("/api/v1/biometrics/enrollments", EnrollmentBody());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var badBase64 = await client.PostAsJsonAsync("/api/v1/biometrics/enrollments",
            EnrollmentBody() with { ImageBase64 = "not-base64!!" });
        Assert.Equal(HttpStatusCode.BadRequest, badBase64.StatusCode);
        using var badMedia = await client.PostAsJsonAsync("/api/v1/biometrics/enrollments",
            EnrollmentBody() with { MediaType = "image/gif" });
        Assert.Equal(HttpStatusCode.BadRequest, badMedia.StatusCode);
        using var badSize = await client.PostAsJsonAsync("/api/v1/biometrics/enrollments",
            EnrollmentBody() with { Width = 4096 });
        Assert.Equal(HttpStatusCode.BadRequest, badSize.StatusCode);

        using (var scope = factory.Services.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<IamDbContext>().BiometricEnrollments.CountAsync());
    }

    private static WebApplicationFactory<Program> ConfiguredFactory(bool withExtractor = true, bool withKey = true) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var values = new Dictionary<string, string?>();
                if (withKey)
                {
                    values[BiometricSettings.Key] = TestKey;
                    values[BiometricSettings.KeyVersion] = "test-1";
                }
                configuration.AddInMemoryCollection(values);
            });
            if (withExtractor)
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IFaceEnrollmentExtractor>();
                    services.AddSingleton<IFaceEnrollmentExtractor>(new TestEnrollmentExtractor());
                });
        });

    private sealed record EnrollmentRequest(string? UserPublicId, string MediaType, int Width, int Height, string ImageBase64);

    private static EnrollmentRequest EnrollmentBody() => new(null, "image/jpeg", 160, 160,
        Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-face-image")));

    private static async Task RegisterAsync(HttpClient client, string username)
    {
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var registered = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = Password, confirmPassword = Password });
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
    }

    private static async Task LoginAsync(HttpClient client, string username, string? password = null)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username, password = password ?? Password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
    }

    private static async Task<string> GetUserPublicIdAsync(WebApplicationFactory<Program> factory, string username)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByNameAsync(username);
        Assert.NotNull(user);
        return user.PublicId!;
    }

    private sealed class TestEnrollmentExtractor : IFaceEnrollmentExtractor
    {
        public string ModelId => "test-face-model";
        public string ModelVersion => "contract-v1";
        public string AdapterId => "test-face-adapter";

        public ValueTask<FaceEmbeddingV1> ExtractAsync(FaceCropV1 crop, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!crop.IsValid) throw new ArgumentException("Recorte inválido.", nameof(crop));
            return ValueTask.FromResult(new FaceEmbeddingV1(FaceEmbeddingV1.CurrentSchemaVersion,
                ModelId, ModelVersion, 3, [0.1f, 0.2f, 0.3f]));
        }
    }

    private static class BiometricSettings
    {
        public const string Key = "BIOMETRIC_ENCRYPTION_KEY";
        public const string KeyVersion = "BIOMETRIC_ENCRYPTION_KEY_VERSION";
    }
}
