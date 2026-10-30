using GarageWatch.Application.Profiles;
using GarageWatch.Domain;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Identity;
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
    private readonly string _databasePath;
    private readonly string? _bootstrapUsername;
    private readonly string? _bootstrapPassword;

    public ApiFactory(ISystemProfileSource? profileSource = null, ILoggerProvider? loggerProvider = null,
        ISegmentCaptureAdapter? captureAdapter = null, string? databasePath = null,
        string? bootstrapUsername = "contract-user", string? bootstrapPassword = "Contract-Password-123!")
    {
        _profileSource = profileSource;
        _loggerProvider = loggerProvider;
        _captureAdapter = captureAdapter;
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "garagewatch-tests"));
        _databasePath = databasePath ?? Path.Combine(Path.GetTempPath(), "garagewatch-tests", $"{Guid.NewGuid():N}.db");
        _bootstrapUsername = bootstrapUsername;
        _bootstrapPassword = bootstrapPassword;
    }

    public string DatabasePath => _databasePath;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseWebRoot(Environment.GetEnvironmentVariable("GARAGEWATCH_WEB_ROOT") ?? "wwwroot");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AUTH_USERNAME"] = _bootstrapUsername,
            ["AUTH_PASSWORD"] = _bootstrapPassword,
            ["AUTH_EMAIL"] = "contract-admin@example.invalid",
            ["IDENTITY_DB_PATH"] = _databasePath
        }));
        if (_loggerProvider is not null)
        {
            builder.ConfigureLogging(logging => logging.AddProvider(_loggerProvider));
        }

        builder.ConfigureTestServices(services =>
        {
            services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
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
