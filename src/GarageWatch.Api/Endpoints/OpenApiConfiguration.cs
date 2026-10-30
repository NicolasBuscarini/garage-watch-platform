using NSwag;
using NSwag.Generation.AspNetCore;
using NSwag.Generation.Processors;
using NSwag.Generation.Processors.Contexts;
using NSwag.Generation.Processors.Security;
using Microsoft.AspNetCore.Http.Metadata;
using NJsonSchema;

namespace GarageWatch.Api.Endpoints;

public static class OpenApiConfiguration
{
    public static bool IsEnabled(IWebHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing");

    public static void AddDocumentation(IServiceCollection services, IWebHostEnvironment environment)
    {
        if (!IsEnabled(environment)) return;

        services.AddEndpointsApiExplorer();
        services.AddOpenApiDocument(settings =>
        {
            settings.DocumentName = "v1";
            settings.Title = "GarageWatch Platform API";
            settings.Version = "1.0";
            settings.Description = "Local development API. Operations are grouped by domain and retain server-side authorization.";
            settings.AddSecurity("SessionCookie", [], new OpenApiSecurityScheme
            {
                Type = OpenApiSecuritySchemeType.ApiKey,
                Name = "garagewatch.session",
                In = OpenApiSecurityApiKeyLocation.Cookie,
                Description = "Same-origin ASP.NET Core Identity cookie; the browser sends the logged-in Administrator session."
            });
            settings.OperationProcessors.Add(new AspNetCoreOperationSecurityScopeProcessor("SessionCookie"));
            settings.OperationProcessors.Add(new EndpointDocumentationProcessor());
        });
    }

    public static void UseDocumentation(this WebApplication app)
    {
        if (!IsEnabled(app.Environment)) return;

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/docs") &&
                !context.Request.Path.StartsWithSegments("/openapi"))
            {
                await next();
                return;
            }

            if (context.User.Identity?.IsAuthenticated != true)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (!context.User.IsInRole("Administrator"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next();
        });

        app.UseOpenApi(settings => settings.Path = "/openapi/{documentName}.json");
        app.UseSwaggerUi(settings =>
        {
            settings.Path = "/docs";
            settings.DocumentPath = "/openapi/v1.json";
        });
    }
}

public sealed class EndpointDocumentationProcessor : IOperationProcessor
{
    public bool Process(OperationProcessorContext context)
    {
        if (context is not AspNetCoreOperationProcessorContext aspNetCore ||
            aspNetCore.ApiDescription.ActionDescriptor.EndpointMetadata is not { } metadata)
            return true;

        var operation = context.OperationDescription.Operation;
        operation.Summary = metadata.OfType<IEndpointSummaryMetadata>().FirstOrDefault()?.Summary ?? operation.Summary;
        operation.Description = metadata.OfType<IEndpointDescriptionMetadata>().FirstOrDefault()?.Description
            ?? operation.Description;

        foreach (var (status, response) in operation.Responses)
            if (string.IsNullOrWhiteSpace(response.Description))
                response.Description = DescribeResponse(status);

        if (operation.OperationId == "GetLivePlaylist" && operation.Responses.TryGetValue("200", out var playlist))
        {
            playlist.Content.Clear();
            playlist.Content["application/vnd.apple.mpegurl"] = new OpenApiMediaType
            {
                Schema = new JsonSchema { Type = JsonObjectType.String }
            };
        }

        if (operation.OperationId == "GetLiveSegment")
        {
            foreach (var status in new[] { "200", "206" })
                if (operation.Responses.TryGetValue(status, out var segment))
                {
                    segment.Content.Clear();
                    segment.Content["video/mp2t"] = new OpenApiMediaType
                    {
                        Schema = new JsonSchema { Type = JsonObjectType.String, Format = "binary" }
                    };
                }
        }

        return true;
    }

    private static string DescribeResponse(string status) => status switch
    {
        "200" => "The request completed successfully. The response body follows the documented schema/content type.",
        "201" => "The resource was created. The response body contains the created representation and Location identifies it.",
        "202" => "The request was accepted. This endpoint returns no representation body.",
        "204" => "The request completed successfully and returns no representation body.",
        "206" => "The requested byte range was returned; Content-Range describes the selected segment bytes.",
        "400" => "The request is invalid. If a body is present, it contains the documented error code.",
        "401" => "Authentication is required or the current session is no longer valid.",
        "403" => "The authenticated session is not authorized, or the same-origin check failed.",
        "404" => "The requested resource is unavailable or intentionally hidden from this session.",
        "409" => "The request conflicts with current resource state or its optimistic version.",
        "429" => "A rate, lease-capacity, or service limit was reached; retry after the condition clears.",
        "500" => "An unexpected server error occurred. The safe error body includes a correlation ID for diagnosis.",
        "503" => "The service or live source is not currently ready; retry after readiness is restored.",
        _ => "The response uses the documented status and schema/content type."
    };
}
