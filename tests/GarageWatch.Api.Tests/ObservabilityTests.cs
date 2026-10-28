using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Logging;
using GarageWatch.Api.Correlation;
using GarageWatch.Api.Observability;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class ObservabilityTests
{
    [Fact]
    public async Task Use_case_span_inherits_w3c_trace_and_safe_correlation()
    {
        var activities = new ConcurrentBag<Activity>();
        using var listener = CreateActivityListener(activities);
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        const string correlationId = "6f9619ff-8b86-d011-b42d-00cf4fc964ff";
        client.DefaultRequestHeaders.Add("traceparent", $"00-{traceId}-00f067aa0ba902b7-01");
        client.DefaultRequestHeaders.Add(CorrelationMiddleware.HeaderName, correlationId);

        using var response = await client.GetAsync("/api/v1/system-profile");
        var useCase = Assert.Single(activities, activity =>
            Equals(activity.GetTagItem("correlation.id"), correlationId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(traceId, useCase.TraceId.ToHexString());
        Assert.NotEqual(default, useCase.ParentSpanId);
        Assert.Equal("GetSystemProfileQuery", useCase.GetTagItem("use_case"));
        Assert.Equal("success", useCase.GetTagItem("outcome"));
        Assert.Equal(ActivityStatusCode.Ok, useCase.Status);
    }

    [Fact]
    public async Task Use_case_metrics_have_bounded_dimensions_not_request_ids()
    {
        var measurements = new ConcurrentBag<MetricMeasurement>();
        using var listener = CreateMeterListener(measurements);
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        foreach (var correlationId in new[]
                 {
                     "6f9619ff-8b86-d011-b42d-00cf4fc964ff",
                     "7f9619ff-8b86-d011-b42d-00cf4fc964ff"
                 })
        {
            client.DefaultRequestHeaders.Remove(CorrelationMiddleware.HeaderName);
            client.DefaultRequestHeaders.Add(CorrelationMiddleware.HeaderName, correlationId);
            using var response = await client.GetAsync("/api/v1/system-profile");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var executions = measurements.Where(measurement =>
            measurement.InstrumentName == "garagewatch.use_case.executions").ToArray();
        var durations = measurements.Where(measurement =>
            measurement.InstrumentName == "garagewatch.use_case.duration").ToArray();

        Assert.Equal(2, executions.Length);
        Assert.Equal(2, durations.Length);
        Assert.All(executions.Concat(durations), measurement =>
        {
            Assert.Equal("GetSystemProfileQuery", measurement.Tags["use_case"]);
            Assert.Equal("success", measurement.Tags["outcome"]);
            Assert.DoesNotContain("correlation_id", measurement.Tags.Keys);
            Assert.DoesNotContain("correlation.id", measurement.Tags.Keys);
            Assert.DoesNotContain("trace_id", measurement.Tags.Keys);
            Assert.DoesNotContain("camera_id", measurement.Tags.Keys);
        });
        Assert.All(durations, measurement => Assert.True((double)measurement.Value >= 0));
    }

    [Fact]
    public async Task Unexpected_exception_marks_span_and_metric_without_exporting_message()
    {
        const string correlationId = "8f9619ff-8b86-d011-b42d-00cf4fc964ff";
        var activities = new ConcurrentBag<Activity>();
        var measurements = new ConcurrentBag<MetricMeasurement>();
        using var activityListener = CreateActivityListener(activities);
        using var meterListener = CreateMeterListener(measurements);
        using var factory = new ApiFactory(new ThrowingSystemProfileSource());
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationMiddleware.HeaderName, correlationId);

        using var response = await client.GetAsync("/api/v1/system-profile");
        var useCase = Assert.Single(activities, activity =>
            Equals(activity.GetTagItem("correlation.id"), correlationId));
        var exceptionMetric = Assert.Single(measurements, measurement =>
            measurement.InstrumentName == "garagewatch.use_case.executions"
            && Equals(measurement.Tags.GetValueOrDefault("outcome"), "exception"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ActivityStatusCode.Error, useCase.Status);
        Assert.Equal("exception", useCase.GetTagItem("outcome"));
        Assert.Equal(typeof(InvalidOperationException).FullName, useCase.GetTagItem("error.type"));
        Assert.DoesNotContain("secret-value-must-not-leak", string.Join('|', useCase.Tags
            .Select(tag => $"{tag.Key}:{tag.Value}")), StringComparison.Ordinal);
        Assert.Equal("GetSystemProfileQuery", exceptionMetric.Tags["use_case"]);
        Assert.DoesNotContain("secret-value-must-not-leak", string.Join('|', exceptionMetric.Tags.Values),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Structured_use_case_log_contains_matching_trace_and_correlation_without_exception_text()
    {
        const string correlationId = "9f9619ff-8b86-d011-b42d-00cf4fc964ff";
        var loggerProvider = new InMemoryLoggerProvider();
        using var factory = new ApiFactory(new ThrowingSystemProfileSource(), loggerProvider);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(CorrelationMiddleware.HeaderName, correlationId);

        using var response = await client.GetAsync("/api/v1/system-profile");
        var logs = loggerProvider.Records.ToArray();
        var useCaseLog = Assert.Single(logs, record => record.Message.Contains(
            "Caso de uso concluído", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(correlationId, useCaseLog.Scopes["correlation_id"]);
        Assert.Matches("^[0-9a-f]{32}$", useCaseLog.Scopes["trace_id"] as string ?? string.Empty);
        Assert.Matches("^[0-9a-f]{16}$", useCaseLog.Scopes["span_id"] as string ?? string.Empty);
        var combinedLogValues = string.Join("|", logs.SelectMany(record =>
            new[] { record.Message }.Concat(record.Scopes.Values.Select(value => value?.ToString() ?? string.Empty))));
        Assert.DoesNotContain("secret-value-must-not-leak", combinedLogValues, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_query_values_are_not_written_to_traces_or_application_logs()
    {
        const string querySentinel = "private-query-sentinel";
        var activities = new ConcurrentBag<Activity>();
        var loggerProvider = new InMemoryLoggerProvider();
        using var listener = CreateHttpAndUseCaseActivityListener(activities);
        using var factory = new ApiFactory(loggerProvider: loggerProvider);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/v1/system-profile?token={querySentinel}");
        var traceValues = string.Join("|", activities.SelectMany(activity =>
            activity.Tags.Select(tag => $"{tag.Key}:{tag.Value}")
                .Concat(activity.Events.SelectMany(activityEvent => activityEvent.Tags
                    .Select(tag => $"{tag.Key}:{tag.Value}")))));
        var logValues = string.Join("|", loggerProvider.Records.SelectMany(record =>
            new[] { record.Message }.Concat(record.Scopes.Values.Select(value => value?.ToString() ?? string.Empty))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(querySentinel, traceValues, StringComparison.Ordinal);
        Assert.DoesNotContain(querySentinel, logValues, StringComparison.Ordinal);
    }

    [Fact]
    public void Otlp_endpoint_is_optional_and_invalid_values_fail_without_echo()
    {
        Assert.Null(ObservabilityConfiguration.ParseOtlpEndpoint(null));
        Assert.Null(ObservabilityConfiguration.ParseOtlpEndpoint("  "));
        Assert.Equal(new Uri("http://collector:4318"),
            ObservabilityConfiguration.ParseOtlpEndpoint("http://collector:4318"));

        const string secretEndpoint = "http://user:secret@collector:4318?token=secret";
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ObservabilityConfiguration.ParseOtlpEndpoint(secretEndpoint));

        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    private static ActivityListener CreateActivityListener(ConcurrentBag<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PlatformTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static MeterListener CreateMeterListener(ConcurrentBag<MetricMeasurement> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == PlatformTelemetry.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };

        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add(new MetricMeasurement(instrument.Name, value, tags.ToArray()
                .ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add(new MetricMeasurement(instrument.Name, value, tags.ToArray()
                .ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal))));
        listener.Start();
        return listener;
    }

    private static ActivityListener CreateHttpAndUseCaseActivityListener(ConcurrentBag<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PlatformTelemetry.ActivitySourceName
                || source.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed record MetricMeasurement(
        string InstrumentName,
        object Value,
        Dictionary<string, object?> Tags);

    private sealed class InMemoryLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

        public ConcurrentBag<CapturedLog> Records { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(InMemoryLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
                owner._scopeProvider.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var scopes = new Dictionary<string, object?>(StringComparer.Ordinal);
                owner._scopeProvider.ForEachScope((scope, values) =>
                {
                    if (scope is IEnumerable<KeyValuePair<string, object?>> properties)
                    {
                        foreach (var property in properties)
                        {
                            values[property.Key] = property.Value;
                        }
                    }
                }, scopes);

                owner.Records.Add(new CapturedLog(formatter(state, exception), scopes));
            }
        }
    }

    private sealed record CapturedLog(string Message, Dictionary<string, object?> Scopes);
}
