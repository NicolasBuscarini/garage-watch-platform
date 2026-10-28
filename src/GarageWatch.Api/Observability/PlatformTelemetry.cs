using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace GarageWatch.Api.Observability;

public static class PlatformTelemetry
{
    public const string ActivitySourceName = "GarageWatch.Platform";
    public const string MeterName = "GarageWatch.Platform";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> UseCaseExecutions = Meter.CreateCounter<long>(
        "garagewatch.use_case.executions",
        unit: "{execution}",
        description: "Quantidade de execuções de casos de uso por resultado.");
    private static readonly Histogram<double> UseCaseDuration = Meter.CreateHistogram<double>(
        "garagewatch.use_case.duration",
        unit: "s",
        description: "Duração de execução de casos de uso.");

    public static Activity? StartUseCase(string useCaseName, string correlationId)
    {
        var activity = ActivitySource.StartActivity("use_case.execute", ActivityKind.Internal);
        activity?.SetTag("use_case", useCaseName);
        activity?.SetTag("correlation.id", correlationId);
        return activity;
    }

    public static void RecordUseCase(string useCaseName, string outcome, double durationSeconds)
    {
        var tags = new TagList
        {
            { "use_case", useCaseName },
            { "outcome", outcome }
        };

        UseCaseExecutions.Add(1, tags);
        UseCaseDuration.Record(durationSeconds, tags);
    }
}
