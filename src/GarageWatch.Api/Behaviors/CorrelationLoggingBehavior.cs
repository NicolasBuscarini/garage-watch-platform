using System.Diagnostics;
using GarageWatch.Api.Correlation;
using GarageWatch.Api.Observability;
using MediatR;

namespace GarageWatch.Api.Behaviors;

public sealed class CorrelationLoggingBehavior<TRequest, TResponse>(
    ICorrelationContext correlationContext,
    ILogger<CorrelationLoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var useCaseName = typeof(TRequest).Name;
        using var activity = PlatformTelemetry.StartUseCase(useCaseName, correlationContext.CorrelationId);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["correlation_id"] = correlationContext.CorrelationId,
            ["use_case"] = useCaseName,
            ["trace_id"] = Activity.Current?.TraceId.ToHexString(),
            ["span_id"] = Activity.Current?.SpanId.ToHexString()
        });

        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var response = await next();
            var durationSeconds = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
            activity?.SetTag("outcome", "success");
            activity?.SetStatus(ActivityStatusCode.Ok);
            PlatformTelemetry.RecordUseCase(useCaseName, "success", durationSeconds);
            logger.LogInformation(
                "Caso de uso concluído. Resultado=sucesso DuraçãoMs={DurationMs}",
                durationSeconds * 1_000);
            return response;
        }
        catch (OperationCanceledException)
        {
            var durationSeconds = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
            activity?.SetTag("outcome", "cancelled");
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            PlatformTelemetry.RecordUseCase(useCaseName, "cancelled", durationSeconds);
            logger.LogWarning(
                "Caso de uso concluído. Resultado=cancelado DuraçãoMs={DurationMs}",
                durationSeconds * 1_000);
            throw;
        }
        catch (Exception exception)
        {
            var durationSeconds = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
            activity?.SetTag("outcome", "exception");
            activity?.SetTag("error.type", exception.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, "unhandled exception");
            PlatformTelemetry.RecordUseCase(useCaseName, "exception", durationSeconds);
            logger.LogWarning(
                "Caso de uso concluído. Resultado=falha DuraçãoMs={DurationMs}",
                durationSeconds * 1_000);
            throw;
        }
    }
}
