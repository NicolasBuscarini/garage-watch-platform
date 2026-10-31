using GarageWatch.Application.Profiles;
using GarageWatch.Application.Identity;
using GarageWatch.Application.Cameras;
using GarageWatch.Application.Authorization;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Infrastructure.Identity.Repositories;
using GarageWatch.Infrastructure.Analysis;
using GarageWatch.Application.Analysis;
using GarageWatch.Infrastructure.Events;
using GarageWatch.Application.Events;
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
        services.AddSingleton<IPublicIdGenerator, PublicIdGenerator>();
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
        services.AddScoped<AccessProfileService>();
        services.AddScoped<AccessRequestService>();
        services.AddScoped<ClipAnalysisDispatchService>();
        services.AddScoped<CameraClipTriggerConfigurationService>();
        services.AddScoped<ICameraCatalog, CameraCatalogService>();
        services.AddScoped<ICameraResourceAccess, CameraResourceAccessService>();
        services.AddSingleton<IPermissionEvaluator, PermissionEvaluator>();
        services.AddSingleton<IamTransactionGate>();
        services.AddScoped<IIamUnitOfWork, IamUnitOfWork>();
        services.AddScoped<ICameraAccessGrantRepository, CameraAccessGrantRepository>();
        services.AddScoped<IIamAuditEventRepository, IamAuditEventRepository>();
        services.AddScoped(provider => IdentityBootstrapOptions.FromConfiguration(
            provider.GetRequiredService<IConfiguration>()));
        var captureOptions = CaptureOptions.FromEnvironment(configuration);
        var triggerOptions = ClipTriggerOptions.FromEnvironment(configuration);
        var clipAnalysisOptions = ClipAnalysisDispatchOptions.FromConfiguration(configuration);
        var detectorOptions = OnnxDetectorOptions.FromConfiguration(configuration);
        var eventOptions = EventEngineOptions.FromConfiguration(configuration, captureOptions);
        var clipArchiveOptions = ClipArchiveOptions.FromConfiguration(configuration, captureOptions);
        services.AddSingleton(captureOptions);
        services.AddSingleton(triggerOptions);
        services.AddSingleton(clipAnalysisOptions);
        services.AddSingleton(detectorOptions);
        services.AddSingleton(eventOptions);
        services.AddSingleton(clipArchiveOptions);
        services.AddSingleton<VersionedEventRuleEngine>();
        services.AddSingleton<LocalEventJournal>();
        services.AddSingleton<LocalSegmentSpool>();
        services.AddSingleton<ISegmentCaptureAdapter>(captureOptions.Profile == "synthetic"
            ? new SyntheticSegmentCaptureAdapter(captureOptions)
            : new FfmpegRtspCaptureAdapter(captureOptions));
        services.AddSingleton<IVisualChangeDetector, FfmpegVisualChangeDetector>();
        services.AddSingleton<IObjectDetector, OnnxObjectDetector>();
        services.AddSingleton<IObjectTracker, GreedyIouObjectTracker>();
        services.AddSingleton<DetectionProcessor>();
        services.AddSingleton<ObjectDetectionWorker>();
        services.AddSingleton<IClipMediaAssembler, LocalClipMediaAssembler>();
        services.AddSingleton<IClipArchiveAdapter, LocalFilesystemClipArchiveAdapter>();
        services.AddSingleton<ClipArchiveWorker>();
        services.AddScoped<ClipLibraryService>();
        services.AddSingleton<IClipAnalysisAutoDispatcher, ClipAnalysisAutoDispatcher>();
        services.AddSingleton<ClipTriggerWorker>();
        services.AddSingleton<CaptureWorker>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ClipTriggerWorker>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ClipArchiveWorker>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ObjectDetectionWorker>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<CaptureWorker>());
        return services;
    }
}
