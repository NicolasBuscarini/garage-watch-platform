using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Api.Correlation;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class SystemProfileEndpointTests
{
    [Fact]
    public async Task Profile_executes_real_use_case_and_preserves_opaque_correlation()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        const string requestedId = "6f9619ff-8b86-d011-b42d-00cf4fc964ff";
        client.DefaultRequestHeaders.Add(CorrelationMiddleware.HeaderName, requestedId);

        using var response = await client.GetAsync("/api/v1/system-profile");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(requestedId, response.Headers.GetValues(CorrelationMiddleware.HeaderName).Single());
        Assert.Equal("1.0", body.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(requestedId, body.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal("garage-watch-platform", body.RootElement.GetProperty("serviceName").GetString());
        Assert.True(body.RootElement.TryGetProperty("applicationVersion", out _));
    }

    [Fact]
    public async Task Missing_correlation_is_generated_and_returned()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);

        using var response = await client.GetAsync("/api/v1/system-profile");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var correlationId = response.Headers.GetValues(CorrelationMiddleware.HeaderName).Single();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(Guid.TryParseExact(correlationId, "D", out _));
        Assert.Equal(correlationId, body.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Malformed_correlation_is_rejected_without_reflection()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        client.DefaultRequestHeaders.Add(CorrelationMiddleware.HeaderName, "invalid/value-with-secret");

        using var response = await client.GetAsync("/api/v1/system-profile");
        var bodyText = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(bodyText);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("invalid/value-with-secret", bodyText, StringComparison.Ordinal);
        Assert.Equal("invalid_correlation_id", body.RootElement.GetProperty("code").GetString());
        Assert.Equal(response.Headers.GetValues(CorrelationMiddleware.HeaderName).Single(),
            body.RootElement.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Unexpected_failure_returns_safe_error_and_does_not_leak_exception()
    {
        using var factory = new ApiFactory(new ThrowingSystemProfileSource());
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);

        using var response = await client.GetAsync("/api/v1/system-profile");
        var bodyText = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("secret-value-must-not-leak", bodyText, StringComparison.Ordinal);
        Assert.Contains("internal_error", bodyText, StringComparison.Ordinal);
        Assert.NotEmpty(response.Headers.GetValues(CorrelationMiddleware.HeaderName).Single());
    }

    [Fact]
    public async Task Health_endpoints_return_minimal_status_without_dependency_details()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        await AuthenticateAsync(client);

        using (var live = await client.GetAsync("/health/live"))
        using (var liveBody = JsonDocument.Parse(await live.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal("live", liveBody.RootElement.GetProperty("status").GetString());
            Assert.Single(liveBody.RootElement.EnumerateObject());
        }

        using var response = await WaitForReadyAsync(client, TimeSpan.FromSeconds(5));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ready", body.RootElement.GetProperty("status").GetString());
        var capture = body.RootElement.GetProperty("capture");
        Assert.Equal("synthetic-segment-source", capture.GetProperty("adapter").GetString());
        Assert.Equal("1.0", capture.GetProperty("adapterVersion").GetString());
        Assert.Contains("synthetic-segments", capture.GetProperty("capabilities").EnumerateArray()
            .Select(value => value.GetString()));
        Assert.Equal("capturing", capture.GetProperty("state").GetString());
        Assert.DoesNotContain("RTSP", body.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AuthenticateAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "contract-user", password = "Contract-Password-123!" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").Single().Split(';', 2)[0]);
    }

    private static async Task<HttpResponseMessage> WaitForReadyAsync(HttpClient client, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        HttpResponseMessage? response = null;
        while (DateTime.UtcNow < deadline)
        {
            response?.Dispose();
            response = await client.GetAsync("/health/ready");
            if (response.StatusCode == HttpStatusCode.OK) return response;
            await Task.Delay(50);
        }
        return response ?? await client.GetAsync("/health/ready");
    }

    [Fact]
    public async Task Capture_failure_degrades_readiness_without_affecting_liveness()
    {
        using var factory = new ApiFactory(captureAdapter: new UnavailableCaptureAdapter());
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        HttpResponseMessage? readiness = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            readiness?.Dispose();
            readiness = await client.GetAsync("/health/ready");
            if (readiness.StatusCode == HttpStatusCode.ServiceUnavailable
                && (await readiness.Content.ReadAsStringAsync()).Contains("\"state\":\"reconnecting\"", StringComparison.Ordinal)) break;
            await Task.Delay(25);
        }

        Assert.NotNull(readiness);
        using (readiness)
        using (var body = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
            Assert.Equal("degraded", body.RootElement.GetProperty("status").GetString());
            Assert.Equal("reconnecting", body.RootElement.GetProperty("capture").GetProperty("state").GetString());
            Assert.Equal("capture_source_unavailable", body.RootElement.GetProperty("capture").GetProperty("lastErrorCode").GetString());
        }

        using var cameraStatus = await client.GetAsync("/api/v1/cameras");
        using var cameras = JsonDocument.Parse(await cameraStatus.Content.ReadAsStringAsync());
        Assert.Equal("reconnecting", cameras.RootElement[0].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, cameras.RootElement[0].GetProperty("streamEpoch").ValueKind);
    }

    [Fact]
    public async Task Response_matches_versioned_contract_schema()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        await AuthenticateAsync(client);
        using var response = await client.GetAsync("/api/v1/system-profile");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using var schema = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "contracts", "system-profile-response.v1.schema.json")));

        var required = schema.RootElement.GetProperty("required").EnumerateArray()
            .Select(element => element.GetString() ?? throw new InvalidOperationException("Schema required field cannot be null."))
            .ToHashSet(StringComparer.Ordinal);
        var schemaProperties = schema.RootElement.GetProperty("properties").EnumerateObject()
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var properties = body.RootElement.EnumerateObject()
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal("https://json-schema.org/draft/2020-12/schema",
            schema.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.True(required.SetEquals(properties));
        Assert.True(required.SetEquals(schemaProperties));
        Assert.Equal("1.0", body.RootElement.GetProperty("schemaVersion").GetString());
        foreach (var property in body.RootElement.EnumerateObject())
        {
            var definition = schema.RootElement.GetProperty("properties").GetProperty(property.Name);
            Assert.Equal("string", definition.GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.String, property.Value.ValueKind);
            Assert.NotEmpty(property.Value.GetString()!);
            if (definition.TryGetProperty("const", out var expected))
            {
                Assert.Equal(expected.GetString(), property.Value.GetString());
            }
        }

        Assert.True(Guid.TryParseExact(body.RootElement.GetProperty("correlationId").GetString(), "D", out _));
    }
}
