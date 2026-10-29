using GarageWatch.Application.Profiles;
using GarageWatch.Infrastructure.Capture;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GarageWatch.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPlatformInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ISystemProfileSource, EnvironmentSystemProfileSource>();
        var captureOptions = CaptureOptions.FromEnvironment(configuration);
        services.AddSingleton(captureOptions);
        services.AddSingleton<LocalSegmentSpool>();
        services.AddSingleton<ISegmentCaptureAdapter>(captureOptions.Profile == "synthetic"
            ? new SyntheticSegmentCaptureAdapter(captureOptions)
            : new FfmpegRtspCaptureAdapter(captureOptions));
        services.AddSingleton<CaptureWorker>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<CaptureWorker>());
        return services;
    }
}
