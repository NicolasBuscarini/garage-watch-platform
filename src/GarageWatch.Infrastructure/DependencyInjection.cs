using GarageWatch.Application.Profiles;
using GarageWatch.Application.Identity;
using GarageWatch.Application.Cameras;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Identity.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GarageWatch.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPlatformInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<ISystemProfileSource, EnvironmentSystemProfileSource>();
        services.AddDbContext<IamDbContext>((provider, options) =>
        {
            var settings = provider.GetRequiredService<IConfiguration>();
            var databasePath = settings[IdentityBootstrapOptions.DatabasePathKey] ?? IdentityBootstrapOptions.DefaultDatabasePath;
            if (!Path.IsPathFullyQualified(databasePath))
                throw new InvalidOperationException("IDENTITY_DB_PATH deve ser um caminho absoluto.");
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 5,
                Pooling = true
            }.ToString();
            options.UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly(typeof(IamDbContext).Assembly.FullName));
        });
        services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.User.RequireUniqueEmail = false;
            options.Password.RequiredLength = 6;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<IamDbContext>()
            .AddSignInManager();
        services.AddScoped<IamDatabaseInitializer>();
        services.AddScoped<IdentityRegistrationService>();
        services.AddScoped<IdentityAdministrationService>();
        services.AddScoped<ICameraCatalog, CameraCatalogService>();
        services.AddScoped<ICameraResourceAccess, CameraResourceAccessService>();
        services.AddSingleton<IamTransactionGate>();
        services.AddScoped<IIamUnitOfWork, IamUnitOfWork>();
        services.AddScoped<ICameraAccessGrantRepository, CameraAccessGrantRepository>();
        services.AddScoped<IIamAuditEventRepository, IamAuditEventRepository>();
        services.AddScoped(provider => IdentityBootstrapOptions.FromConfiguration(
            provider.GetRequiredService<IConfiguration>()));
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
