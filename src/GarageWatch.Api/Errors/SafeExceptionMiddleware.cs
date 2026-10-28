using GarageWatch.Api.Correlation;
using GarageWatch.Api.Errors;

namespace GarageWatch.Api.Errors;

public sealed class SafeExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ICorrelationContext correlationContext,
        ILogger<SafeExceptionMiddleware> logger)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                "Falha inesperada na requisição. Tipo={FailureType}",
                exception.GetType().Name);

            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.Headers[CorrelationMiddleware.HeaderName] = correlationContext.CorrelationId;
            await context.Response.WriteAsJsonAsync(
                new ErrorResponse("internal_error", correlationContext.CorrelationId),
                context.RequestAborted);
        }
    }
}
