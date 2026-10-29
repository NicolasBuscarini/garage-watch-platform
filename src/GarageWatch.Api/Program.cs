using GarageWatch.Api.Behaviors;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Correlation;
using GarageWatch.Api.Presence;
using GarageWatch.Api.Errors;
using GarageWatch.Api.Live;
using GarageWatch.Api.Observability;
using GarageWatch.Api.Security;
using GarageWatch.Application.Profiles;
using GarageWatch.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Services.AddScoped<CorrelationContext>();
builder.Services.AddScoped<ICorrelationContext>(services => services.GetRequiredService<CorrelationContext>());
builder.Services.AddPlatformInfrastructure(builder.Configuration);
builder.Services.AddSingleton<LivePlaylistSequence>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<ClientPresenceRegistry>();
builder.Services.AddSingleton(provider => LocalAuthenticationOptions.FromEnvironment(
    provider.GetRequiredService<IConfiguration>()));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "garagewatch.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization(options => options.AddPolicy("Viewer", policy => policy.RequireRole("Viewer", "Administrator")));
builder.Services.AddRateLimiter(options => options.AddFixedWindowLimiter("login", limiter =>
{
    limiter.PermitLimit = 5;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.QueueLimit = 0;
}));
builder.Services.AddPlatformObservability(builder.Configuration, builder.Environment);
builder.Services.AddMediatR(configuration =>
{
    configuration.RegisterServicesFromAssembly(typeof(GetSystemProfileQuery).Assembly);
    configuration.AddOpenBehavior(typeof(CorrelationLoggingBehavior<,>));
    configuration.AddOpenBehavior(typeof(ExceptionLoggingBehavior<,>));
});

var app = builder.Build();
_ = app.Services.GetRequiredService<LocalAuthenticationOptions>();
app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<SafeExceptionMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    await next();
});

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (GarageWatch.Infrastructure.Capture.CaptureWorker capture,
    GarageWatch.Infrastructure.Capture.CaptureOptions captureOptions) =>
{
    var ready = capture.Status.State == "capturing";
    var response = new
    {
        status = ready ? "ready" : "degraded",
        capture = new
        {
            adapter = capture.Status.Adapter,
            adapterVersion = capture.Status.AdapterVersion,
            capabilities = capture.Status.Capabilities,
            state = capture.Status.State,
            capture.Status.StreamEpoch,
            capture.Status.Gaps,
            capture.Status.Segments,
            capture.Status.LastErrorCode,
            spool = new
            {
                status = capture.Status.State == "spool-unavailable" ? "unavailable" : "available",
                segments = capture.Status.Segments,
                segmentLimit = captureOptions.MaximumSegments
            }
        }
    };
    return Results.Json(response, statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
}).RequireAuthorization("Viewer");
app.MapPost("/api/v1/auth/login", async (LoginRequest request, LocalAuthenticationOptions credentials,
    HttpContext context) =>
{
    if (request.Username?.Length > 64 || request.Password?.Length > 256 ||
        !credentials.Verify(request.Username, request.Password))
        return Results.Unauthorized();
    var claims = new[] { new Claim(ClaimTypes.Name, credentials.Username), new Claim(ClaimTypes.Role, credentials.Role) };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.NoContent();
}).AllowAnonymous().RequireRateLimiting("login");
app.MapPost("/api/v1/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization("Viewer");
app.MapGet("/api/v1/auth/session", (ClaimsPrincipal user) =>
    Results.Ok(new { authenticated = user.Identity?.IsAuthenticated == true })).AllowAnonymous();
app.MapGet("/api/v1/auth/me", (ClaimsPrincipal user) => Results.Ok(new AuthenticatedUser(
    user.Identity?.Name ?? "", user.FindFirstValue(ClaimTypes.Role) ?? "Viewer"))).RequireAuthorization("Viewer");
app.MapPut("/api/v1/clients/presence", (ClientPresenceHeartbeat request, ClientPresenceRegistry registry) =>
{
    if (request.ClientId == Guid.Empty) return Results.BadRequest(new { code = "invalid_client_id" });
    if (!registry.Heartbeat(request.ClientId, out var activeClients))
        return Results.Json(new { code = "client_capacity_reached", activeClients },
            statusCode: StatusCodes.Status429TooManyRequests);
    return Results.Ok(new { activeClients, leaseSeconds = ClientPresenceRegistry.LeaseDuration.TotalSeconds });
}).RequireAuthorization("Viewer");
app.MapGet("/api/v1/cameras", (GarageWatch.Infrastructure.Capture.CaptureOptions captureOptions,
    GarageWatch.Infrastructure.Capture.CaptureWorker capture) => Results.Ok(new[] { new
    {
        cameraId = captureOptions.CameraId,
        status = capture.Status.State,
        adapter = capture.Status.Adapter,
        streamEpoch = capture.Status.StreamEpoch,
        segments = capture.Status.Segments,
        gaps = capture.Status.Gaps,
        liveUrl = $"/api/v1/live/{captureOptions.CameraId}/index.m3u8"
    }
})).RequireAuthorization("Viewer");
app.MapGet("/api/v1/live/{cameraId}/index.m3u8", (string cameraId,
    GarageWatch.Infrastructure.Capture.CaptureOptions captureOptions,
    GarageWatch.Infrastructure.Capture.LocalSegmentSpool spool,
    LivePlaylistSequence playlistSequence) =>
{
    if (!string.Equals(cameraId, captureOptions.CameraId, StringComparison.Ordinal)) return Results.NotFound();
    var segments = spool.GetRecentSegments(6);
    if (segments.Count == 0) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    var target = Math.Max(1, (int)Math.Ceiling(segments.Max(x => (x.Manifest.EndedAt - x.Manifest.StartedAt).TotalSeconds)));
    var mediaSequence = playlistSequence.GetStartSequence(segments.Select(x => x.Manifest.SegmentId).ToArray(),
        captureOptions.MaximumSegments);
    var playlist = new System.Text.StringBuilder().AppendLine("#EXTM3U").AppendLine("#EXT-X-VERSION:3")
        .AppendLine($"#EXT-X-TARGETDURATION:{target}").AppendLine($"#EXT-X-MEDIA-SEQUENCE:{mediaSequence}");
    for (var index = 0; index < segments.Count; index++)
    {
        var segment = segments[index];
        if (LivePlaylistSequence.StartsDiscontinuity(segments.Select(item => item.Manifest.StreamEpoch).ToArray(), index))
            playlist.AppendLine("#EXT-X-DISCONTINUITY");
        var duration = Math.Max(0.1, (segment.Manifest.EndedAt - segment.Manifest.StartedAt).TotalSeconds);
        playlist.AppendLine($"#EXTINF:{duration:F3},").AppendLine($"/api/v1/live/{cameraId}/segments/{segment.Manifest.SegmentId}");
    }
    return Results.Text(playlist.ToString(), "application/vnd.apple.mpegurl");
}).RequireAuthorization("Viewer");
app.MapGet("/api/v1/live/{cameraId}/segments/{segmentId}", (string cameraId, string segmentId,
    GarageWatch.Infrastructure.Capture.CaptureOptions captureOptions,
    GarageWatch.Infrastructure.Capture.LocalSegmentSpool spool) =>
{
    if (!string.Equals(cameraId, captureOptions.CameraId, StringComparison.Ordinal) ||
        !GarageWatch.Infrastructure.Capture.LocalSegmentSpool.IsSafeSegmentId(segmentId)) return Results.NotFound();
    var item = spool.GetRecentSegments(captureOptions.MaximumSegments).FirstOrDefault(x => x.Manifest.SegmentId == segmentId);
    return item.Manifest is null ? Results.NotFound() : Results.File(item.Path, "video/mp2t", enableRangeProcessing: false);
}).RequireAuthorization("Viewer");
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
