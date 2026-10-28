using GarageWatch.Api.Behaviors;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Correlation;
using GarageWatch.Api.Errors;
using GarageWatch.Api.Observability;
using GarageWatch.Application.Profiles;
using GarageWatch.Infrastructure;
using MediatR;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.Sources.Clear();
builder.Configuration.AddEnvironmentVariables();

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.AddScoped<CorrelationContext>();
builder.Services.AddScoped<ICorrelationContext>(services => services.GetRequiredService<CorrelationContext>());
builder.Services.AddPlatformInfrastructure();
builder.Services.AddPlatformObservability(builder.Configuration, builder.Environment);
builder.Services.AddMediatR(configuration =>
{
    configuration.RegisterServicesFromAssembly(typeof(GetSystemProfileQuery).Assembly);
    configuration.AddOpenBehavior(typeof(CorrelationLoggingBehavior<,>));
    configuration.AddOpenBehavior(typeof(ExceptionLoggingBehavior<,>));
});

var app = builder.Build();
app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<SafeExceptionMiddleware>();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
app.MapGet("/api/v1/system-profile", async (
    IMediator mediator,
    ICorrelationContext correlationContext,
    CancellationToken cancellationToken) =>
{
    var profile = await mediator.Send(new GetSystemProfileQuery(), cancellationToken);
    return Results.Ok(new SystemProfileResponseV1(
        "1.0",
        correlationContext.CorrelationId,
        profile.ServiceName,
        profile.Environment,
        profile.ApplicationVersion));
});

app.Run();

public partial class Program { }
