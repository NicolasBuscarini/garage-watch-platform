using GarageWatch.Api.Behaviors;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Correlation;
using GarageWatch.Api.Presence;
using GarageWatch.Api.Errors;
using GarageWatch.Api.Live;
using GarageWatch.Api.Observability;
using GarageWatch.Api.Security;
using GarageWatch.Infrastructure.Identity;
using GarageWatch.Application.Profiles;
using GarageWatch.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Services.AddScoped<CorrelationContext>();
builder.Services.AddScoped<ICorrelationContext>(services => services.GetRequiredService<CorrelationContext>());
builder.Services.AddPlatformInfrastructure(builder.Configuration);
builder.Services.AddSingleton<LivePlaylistSequence>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<ClientPresenceRegistry>();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "garagewatch.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
    options.Events.OnValidatePrincipal = SecurityStampValidator.ValidatePrincipalAsync;
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
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromSeconds(15));
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("Viewer", policy => policy.RequireRole("Viewer", "Administrator"));
    options.AddPolicy("Administrator", policy => policy.RequireRole("Administrator"));
});
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
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<IamDatabaseInitializer>().InitializeAsync();
app.UseMiddleware<CorrelationMiddleware>();
app.UseMiddleware<SafeExceptionMiddleware>();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

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
}).RequireAuthorization("Administrator");
app.MapPost("/api/v1/auth/login", async (LoginRequest request, UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signInManager) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 64 ||
        string.IsNullOrEmpty(request.Password) || request.Password.Length > 256)
        return Results.Unauthorized();
    var account = await users.FindByNameAsync(request.Username);
    if (account is null || !account.IsActive) return Results.Unauthorized();
    var result = await signInManager.PasswordSignInAsync(request.Username, request.Password,
        isPersistent: false, lockoutOnFailure: true);
    if (!result.Succeeded) return Results.Unauthorized();
    return Results.NoContent();
}).AllowAnonymous().RequireRateLimiting("login");
app.MapPost("/api/v1/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(IdentityConstants.ApplicationScheme);
    return Results.NoContent();
}).RequireAuthorization();
app.MapGet("/api/v1/auth/session", (ClaimsPrincipal user) =>
    Results.Ok(new { authenticated = user.Identity?.IsAuthenticated == true })).AllowAnonymous();
app.MapGet("/api/v1/auth/me", async (ClaimsPrincipal principal, UserManager<ApplicationUser> users) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null || !user.IsActive) return Results.Unauthorized();
    var roles = await users.GetRolesAsync(user);
    var primaryRole = roles.Contains("Administrator", StringComparer.Ordinal) ? "Administrator"
        : roles.Contains("Viewer", StringComparer.Ordinal) ? "Viewer" : "User";
    return Results.Ok(new AuthenticatedUser(user.UserName ?? "", primaryRole));
}).RequireAuthorization();
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
}).RequireAuthorization("Administrator");

app.Run();

public partial class Program { }
