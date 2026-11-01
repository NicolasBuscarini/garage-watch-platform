using GarageWatch.Application.Biometrics;
using GarageWatch.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Biometrics;

/// <summary>
/// Periodic sweep that erases expired embeddings. Expiry is durable (absolute timestamp persisted with
/// the row); the worker only shortens the window in which an expired payload remains on disk.
/// </summary>
public sealed class BiometricRetentionWorker(IServiceScopeFactory scopeFactory,
    ILogger<BiometricRetentionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var enrollments = scope.ServiceProvider.GetRequiredService<BiometricEnrollmentService>();
                var expired = await enrollments.ExpireDueAsync(stoppingToken);
                if (expired > 0)
                    logger.LogInformation("Biometrias expiradas removidas: {Count}", expired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Falha ao expirar biometrias; nova tentativa no próximo ciclo.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
