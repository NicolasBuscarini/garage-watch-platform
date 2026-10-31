using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class OpenApiEndpointTests
{
    [Fact]
    public async Task Openapi_and_swagger_ui_require_an_administrator_session()
    {
        await using var factory = new ApiFactory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/docs")).StatusCode);

        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        using var document = await admin.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.Contains("openapi", await document.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var ui = await admin.GetAsync("/docs");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("swagger-ui", await ui.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Openapi_documents_each_http_operation_with_domain_and_typed_contract_metadata()
    {
        await using var factory = new ApiFactory();
        using var admin = await LoginAsync(factory, "contract-user", "Contract-Password-123!");
        using var response = await admin.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var operations = json.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(method => method.Value)).ToArray();
        Assert.Equal(38, operations.Length);
        var operationIds = operations.Select(operation => operation.GetProperty("operationId").GetString()).ToArray();
        Assert.Equal(operationIds.Length, operationIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(operations, operation =>
        {
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("operationId").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("summary").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("description").GetString()));
            Assert.NotEmpty(operation.GetProperty("tags").EnumerateArray());
            Assert.NotEmpty(operation.GetProperty("responses").EnumerateObject());
            Assert.All(operation.GetProperty("responses").EnumerateObject(), response =>
                Assert.False(string.IsNullOrWhiteSpace(response.Value.GetProperty("description").GetString())));
        });

        var requestBodies = operations.Where(operation => operation.TryGetProperty("requestBody", out _)).ToArray();
        Assert.Equal(13, requestBodies.Length);
        Assert.All(requestBodies, operation =>
        {
            var body = operation.GetProperty("requestBody");
            Assert.True(body.GetProperty("content").TryGetProperty("application/json", out var jsonContent));
            Assert.True(jsonContent.TryGetProperty("schema", out _));
        });
        Assert.Contains(operations, operation => operation.GetProperty("operationId").GetString() == "GetLiveSegment" &&
            operation.GetProperty("responses").TryGetProperty("206", out _));
        Assert.Contains(operations, operation => operation.GetProperty("operationId").GetString() == "GetLivePlaylist" &&
            operation.GetProperty("responses").GetProperty("200").GetProperty("content")
                .TryGetProperty("application/vnd.apple.mpegurl", out _));
        Assert.Contains(operations, operation => operation.GetProperty("operationId").GetString() == "AdminReplaceUserAccessProfiles" &&
            operation.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _));
        Assert.Contains(operations, operation => operation.GetProperty("operationId").GetString() == "GetSystemProfile" &&
            operation.TryGetProperty("security", out _));
        Assert.Contains(operations, operation => operation.GetProperty("operationId").GetString() == "GetSession" &&
            !operation.TryGetProperty("security", out _));
    }

    [Fact]
    public async Task Non_administrator_cannot_read_api_documentation_and_non_development_has_no_docs_routes()
    {
        await using (var factory = new ApiFactory())
        {
            using var scope = factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var viewer = new ApplicationUser { Id = Guid.NewGuid(), UserName = "docs-viewer", IsActive = true };
            Assert.True((await users.CreateAsync(viewer, "Viewer-Password-123!")).Succeeded);
            Assert.True((await users.AddToRoleAsync(viewer, "Viewer")).Succeeded);

            using var client = await LoginAsync(factory, "docs-viewer", "Viewer-Password-123!");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/openapi/v1.json")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/docs")).StatusCode);
        }

        await using var productionFactory = new ApiFactory(environment: "Production");
        using var productionAdmin = await LoginAsync(productionFactory, "contract-user", "Contract-Password-123!");
        Assert.Equal(HttpStatusCode.NotFound,
            (await productionAdmin.GetAsync("/openapi/v1.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await productionAdmin.GetAsync("/docs")).StatusCode);
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
