using GarageWatch.Infrastructure.Capture;
using Microsoft.Extensions.DependencyInjection;

namespace GarageWatch.Infrastructure.Identity;

public interface IClipAnalysisAutoDispatcher
{
    Task DispatchAsync(ClipManifestV1 clip, CancellationToken cancellationToken);
}

public sealed class ClipAnalysisAutoDispatcher(IServiceScopeFactory scopes) : IClipAnalysisAutoDispatcher
{
    public async Task DispatchAsync(ClipManifestV1 clip, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ClipAnalysisDispatchService>()
            .AutoRequestAsync(clip, cancellationToken);
    }
}
