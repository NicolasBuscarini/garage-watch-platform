using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace GarageWatch.Api.Observability;

public static class ObservabilityConfiguration
{
    public const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    public static Uri? ParseOtlpEndpoint(string? configuredEndpoint)
    {
        if (string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            return null;
        }

        if (!Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(endpoint.Host)
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException(
                $"{OtlpEndpointVariable} deve ser uma URI HTTP(S) absoluta sem credenciais, query ou fragmento.");
        }

        return endpoint;
    }

    public static IServiceCollection AddPlatformObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var endpoint = ParseOtlpEndpoint(configuration[OtlpEndpointVariable]);
        var serviceVersion = typeof(Program).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "0.0.0";

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: "garage-watch-platform",
                serviceVersion: serviceVersion,
                serviceInstanceId: null,
                autoGenerateServiceInstanceId: false)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment.name"] = environment.EnvironmentName
                }))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = false;
                    options.EnrichWithHttpRequest = (activity, _) =>
                    {
                        activity.SetTag("url.query", null);
                        activity.SetTag("url.full", null);
                        activity.SetTag("http.target", null);
                    };
                });
                tracing.AddSource(PlatformTelemetry.ActivitySourceName);
                if (endpoint is not null)
                {
                    tracing.AddOtlpExporter(options => ConfigureOtlpExporter(options, endpoint));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(PlatformTelemetry.MeterName);
                metrics.AddMeter("GarageWatch.Capture");
                metrics.AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel");
                metrics.AddRuntimeInstrumentation();
                if (endpoint is not null)
                {
                    metrics.AddOtlpExporter(options => ConfigureOtlpExporter(options, endpoint));
                }
            });

        return services;
    }

    private static void ConfigureOtlpExporter(OtlpExporterOptions options, Uri endpoint)
    {
        options.Endpoint = endpoint;
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.TimeoutMilliseconds = 3_000;
        options.MaxRequestSizeBytes = 1_048_576;
        options.BatchExportProcessorOptions.MaxQueueSize = 256;
        options.BatchExportProcessorOptions.MaxExportBatchSize = 64;
        options.BatchExportProcessorOptions.ScheduledDelayMilliseconds = 5_000;
        options.BatchExportProcessorOptions.ExporterTimeoutMilliseconds = 3_000;
    }
}
