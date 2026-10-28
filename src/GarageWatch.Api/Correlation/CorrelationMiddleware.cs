using System.Diagnostics;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;

namespace GarageWatch.Api.Correlation;

public sealed class CorrelationMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context, ICorrelationContext correlationContext)
    {
        var suppliedValues = context.Request.Headers[HeaderName];
        var supplied = suppliedValues.Count == 1 ? suppliedValues[0] : null;
        var isMissing = suppliedValues.Count == 0;
        var isValid = supplied is not null && Guid.TryParseExact(supplied, "D", out _);

        correlationContext.CorrelationId = isValid
            ? Guid.ParseExact(supplied!, "D").ToString("D")
            : Guid.NewGuid().ToString("D");
        Activity.Current?.SetTag("correlation.id", correlationContext.CorrelationId);
        context.Response.Headers[HeaderName] = correlationContext.CorrelationId;

        var scopeState = new Dictionary<string, object?>
        {
            ["correlation_id"] = correlationContext.CorrelationId
        };
        if (Activity.Current is { } activity)
        {
            scopeState["trace_id"] = activity.TraceId.ToHexString();
            scopeState["span_id"] = activity.SpanId.ToHexString();
        }

        using var scope = context.RequestServices
            .GetRequiredService<ILogger<CorrelationMiddleware>>()
            .BeginScope(scopeState);

        if (!isMissing && !isValid)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(
                new ErrorResponse("invalid_correlation_id", correlationContext.CorrelationId),
                context.RequestAborted);
            return;
        }

        await next(context);
    }
}
