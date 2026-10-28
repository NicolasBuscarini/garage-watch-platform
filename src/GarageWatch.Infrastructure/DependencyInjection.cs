using GarageWatch.Application.Profiles;
using Microsoft.Extensions.DependencyInjection;

namespace GarageWatch.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPlatformInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ISystemProfileSource, EnvironmentSystemProfileSource>();
        return services;
    }
}
