using GarageWatch.Application.Profiles;
using GarageWatch.Domain;
using GarageWatch.Infrastructure.Capture;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly ISystemProfileSource? _profileSource;
    private readonly ILoggerProvider? _loggerProvider;
    private readonly ISegmentCaptureAdapter? _captureAdapter;
    private readonly string _authRole;

    public ApiFactory(ISystemProfileSource? profileSource = null, ILoggerProvider? loggerProvider = null,
        ISegmentCaptureAdapter? captureAdapter = null, string authRole = "Administrator")
    {
        _profileSource = profileSource;
        _loggerProvider = loggerProvider;
        _captureAdapter = captureAdapter;
        _authRole = authRole;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AUTH_USERNAME"] = "contract-user",
            ["AUTH_PASSWORD"] = "contract-password-12345",
            ["AUTH_ROLE"] = _authRole
        }));
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
            if (_captureAdapter is not null)
            {
                services.RemoveAll<ISegmentCaptureAdapter>();
                services.AddSingleton(_captureAdapter);
            }
        });
    }
}

public sealed class UnavailableCaptureAdapter : ISegmentCaptureAdapter
{
    public string Name => "contract-unavailable-source";
    public string Version => "1.0";
    public IReadOnlyList<string> Capabilities => ["contract-test"];

    public async IAsyncEnumerable<CapturedSegment> CaptureAsync(string workDirectory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (cancellationToken.IsCancellationRequested) yield break;
        throw new InvalidOperationException("contract_source_unavailable");
    }
}

public sealed class ThrowingSystemProfileSource : ISystemProfileSource
{
    public SystemProfile Get() => throw new InvalidOperationException("secret-value-must-not-leak");
}
