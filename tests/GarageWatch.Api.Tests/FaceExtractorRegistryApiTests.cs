using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Api.Contracts;
using GarageWatch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class FaceExtractorRegistryApiTests
{
    [Fact]
    public async Task Admin_can_register_and_select_version_pinned_evaluation_candidate_without_activating_runtime()
    {
        await using var factory = new ApiFactory();
        using var anonymous = factory.CreateClient();
        using (var denied = await anonymous.GetAsync("/api/v1/admin/face-extractor-registry"))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var admin = await LoginAsync(factory);
        using var missingOrigin = await admin.PostAsJsonAsync("/api/v1/admin/face-extractor-candidates", ValidRequest());
        Assert.Equal(HttpStatusCode.Forbidden, missingOrigin.StatusCode);
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");

        using var invalid = await admin.PostAsJsonAsync("/api/v1/admin/face-extractor-candidates",
            ValidRequest() with { SourceUri = "http://example.invalid/weights" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var queryUri = await admin.PostAsJsonAsync("/api/v1/admin/face-extractor-candidates",
            ValidRequest() with { SourceUri = "https://example.invalid/model?token=must-not-persist" });
        Assert.Equal(HttpStatusCode.BadRequest, queryUri.StatusCode);

        using var created = await admin.PostAsJsonAsync("/api/v1/admin/face-extractor-candidates", ValidRequest());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var candidate = await created.Content.ReadFromJsonAsync<JsonElement>();
        var publicId = candidate.GetProperty("publicId").GetString()!;
        Assert.Matches("^FEX[0-9]{12}$", publicId);
        Assert.Equal("Apache-2.0", candidate.GetProperty("codeLicense").GetString());
        Assert.Equal("LicenseRef-ReviewRequired", candidate.GetProperty("weightsLicense").GetString());

        using var registry = await admin.GetAsync("/api/v1/admin/face-extractor-registry");
        Assert.Equal(HttpStatusCode.OK, registry.StatusCode);
        var emptySelection = await registry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(emptySelection.GetProperty("runtimeEnabled").GetBoolean());
        Assert.False(emptySelection.GetProperty("sampleCollectionEnabled").GetBoolean());
        Assert.True(emptySelection.GetProperty("selectedCandidate").ValueKind is JsonValueKind.Null);

        using var selected = await admin.PutAsJsonAsync("/api/v1/admin/face-extractor-selection",
            new { candidatePublicId = publicId, expectedVersion = 0 });
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        var selection = await selected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, selection.GetProperty("version").GetInt64());
        Assert.Equal(publicId, selection.GetProperty("selectedCandidate").GetProperty("publicId").GetString());
        Assert.False(selection.GetProperty("runtimeEnabled").GetBoolean());
        Assert.False(selection.GetProperty("sampleCollectionEnabled").GetBoolean());

        using var stale = await admin.PutAsJsonAsync("/api/v1/admin/face-extractor-selection",
            new { candidatePublicId = publicId, expectedVersion = 0 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        using var afterSelection = await admin.GetAsync("/api/v1/admin/face-extractor-registry");
        var snapshot = await afterSelection.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, snapshot.GetProperty("version").GetInt64());
        Assert.Equal(publicId, snapshot.GetProperty("selectedCandidate").GetProperty("publicId").GetString());
        Assert.False(snapshot.GetProperty("runtimeEnabled").GetBoolean());
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.Single(await database.FaceExtractorCandidates.ToListAsync());
        Assert.Equal(2, await database.IamAuditEvents.CountAsync(item => item.Action.StartsWith("face_extractor.")));
    }

    [Fact]
    public async Task Candidate_identity_is_immutable_and_duplicate_artifact_is_rejected()
    {
        await using var factory = new ApiFactory();
        using var admin = await LoginAsync(factory);
        admin.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var created = await admin.PostAsJsonAsync("/api/v1/admin/face-extractor-candidates", ValidRequest());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var duplicate = await admin.PostAsJsonAsync("/api/v1/admin/face-extractor-candidates", ValidRequest());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IamDbContext>();
        Assert.Single(await database.FaceExtractorCandidates.ToListAsync());
    }

    private static CreateFaceExtractorCandidateRequest ValidRequest() => new(
        "household-face-model", "1.0.0", new string('a', 64), "https://example.invalid/model-card",
        "Apache-2.0", "LicenseRef-ReviewRequired", "local-onnx-face", "face-embedding.v1");

    private static async Task<HttpClient> LoginAsync(ApiFactory factory)
    {
        var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';', 2)[0]);
        return client;
    }
}
