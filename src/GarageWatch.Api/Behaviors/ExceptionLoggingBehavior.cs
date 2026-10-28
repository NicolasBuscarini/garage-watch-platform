using MediatR;

namespace GarageWatch.Api.Behaviors;

public sealed class ExceptionLoggingBehavior<TRequest, TResponse>(
    ILogger<ExceptionLoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        try
        {
            return await next();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogCritical(
                "Falha inesperada no caso de uso. Caso={UseCase} Tipo={FailureType}",
                typeof(TRequest).Name,
                exception.GetType().Name);
            throw;
        }
    }
}
