using GarageWatch.Application.Profiles;
using GarageWatch.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly ISystemProfileSource? _profileSource;
    private readonly ILoggerProvider? _loggerProvider;

    public ApiFactory(ISystemProfileSource? profileSource = null, ILoggerProvider? loggerProvider = null)
    {
        _profileSource = profileSource;
        _loggerProvider = loggerProvider;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_loggerProvider is not null)
        {
            builder.ConfigureLogging(logging => logging.AddProvider(_loggerProvider));
        }

        builder.ConfigureTestServices(services =>
        {
            var profileSource = _profileSource;
            if (profileSource is not null)
            {
                services.RemoveAll<ISystemProfileSource>();
                services.AddSingleton<ISystemProfileSource>(profileSource);
            }
        });
    }
}

public sealed class ThrowingSystemProfileSource : ISystemProfileSource
{
    public SystemProfile Get() => throw new InvalidOperationException("secret-value-must-not-leak");
}
