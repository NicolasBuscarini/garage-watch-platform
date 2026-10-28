using System.Reflection;
using GarageWatch.Application.Profiles;
using GarageWatch.Domain;
using Microsoft.Extensions.Hosting;

namespace GarageWatch.Infrastructure;

public sealed class EnvironmentSystemProfileSource(IHostEnvironment hostEnvironment) : ISystemProfileSource
{
    public SystemProfile Get()
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

        return new SystemProfile("garage-watch-platform", hostEnvironment.EnvironmentName, version);
    }
}
