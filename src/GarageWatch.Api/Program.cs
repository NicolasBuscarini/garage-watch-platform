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
using GarageWatch.Application.Cameras;
using GarageWatch.Infrastructure;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
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
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("login", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("registration", limiter =>
    {
        limiter.PermitLimit = 3;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});
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
    if (account is null || !account.IsActive)
    {
        _ = users.PasswordHasher.HashPassword(new ApplicationUser(), request.Password);
        return Results.Unauthorized();
    }
    var result = await signInManager.PasswordSignInAsync(request.Username, request.Password,
        isPersistent: false, lockoutOnFailure: true);
    if (!result.Succeeded) return Results.Unauthorized();
    return Results.NoContent();
}).AllowAnonymous().RequireRateLimiting("login");
app.MapPost("/api/v1/auth/register", async (RegisterRequest request, HttpContext context,
    IdentityRegistrationService registration, CancellationToken cancellationToken) =>
{
    if (!IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var outcome = await registration.RegisterAsync(request.Username, request.Password,
        request.ConfirmPassword, cancellationToken);
    return outcome == RegistrationOutcome.InvalidInput
        ? Results.BadRequest(new { code = "invalid_registration" })
        : Results.Accepted();
}).AllowAnonymous().RequireRateLimiting("registration");
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
app.MapGet("/api/v1/cameras", async (ClaimsPrincipal principal, UserManager<ApplicationUser> users,
    ICameraCatalog catalog, GarageWatch.Infrastructure.Capture.CaptureOptions captureOptions,
    GarageWatch.Infrastructure.Capture.CaptureWorker capture, CancellationToken cancellationToken) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null || !user.IsActive) return Results.Unauthorized();
    var isAdministrator = await users.IsInRoleAsync(user, "Administrator");
    if (!isAdministrator && !await users.IsInRoleAsync(user, "Viewer"))
        return Results.Forbid();
    var cameras = await catalog.ListVisibleCamerasAsync(user.Id, isAdministrator, cancellationToken);
    return Results.Ok(cameras.Select(camera => new
    {
        cameraId = camera.CameraId,
        contextId = camera.ContextId,
        name = camera.Name,
        status = camera.IsEnabled && string.Equals(camera.CameraId, captureOptions.CameraId, StringComparison.Ordinal)
            ? capture.Status.State
            : "stopped",
        streamEpoch = camera.IsEnabled && string.Equals(camera.CameraId, captureOptions.CameraId, StringComparison.Ordinal)
            ? capture.Status.StreamEpoch
            : null,
        liveUrl = $"/api/v1/live/{camera.CameraId}/index.m3u8"
    }));
}).RequireAuthorization("Viewer");

app.MapGet("/api/v1/admin/contexts", async (int? page, int? pageSize, ICameraCatalog catalog,
    CancellationToken cancellationToken) =>
    Results.Ok(await catalog.ListContextsAsync(page ?? 1, pageSize ?? 50, cancellationToken)))
    .RequireAuthorization("Administrator");
app.MapPost("/api/v1/admin/contexts", async (CreateCameraContextRequest request, HttpContext context,
    ClaimsPrincipal principal, UserManager<ApplicationUser> users, ICameraCatalog catalog,
    CancellationToken cancellationToken) =>
{
    if (!IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var actor = await users.GetUserAsync(principal);
    if (actor is null) return Results.Unauthorized();
    try
    {
        var created = await catalog.CreateContextAsync(actor.Id, request.ContextId, request.Name, cancellationToken);
        return Results.Created($"/api/v1/admin/contexts/{created.ContextId}", created);
    }
    catch (Exception exception) when (exception is CatalogValidationException or CatalogConflictException or
                                      CatalogNotFoundException or UnauthorizedAccessException)
    {
        return CatalogError(exception);
    }
}).RequireAuthorization("Administrator");
app.MapPut("/api/v1/admin/contexts/{contextId}", async (string contextId, UpdateCameraContextRequest request,
    HttpContext context, ClaimsPrincipal principal, UserManager<ApplicationUser> users, ICameraCatalog catalog,
    CancellationToken cancellationToken) =>
{
    if (!IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var actor = await users.GetUserAsync(principal);
    if (actor is null) return Results.Unauthorized();
    try
    {
        return Results.Ok(await catalog.UpdateContextAsync(actor.Id, contextId, request.Version, request.Name,
            request.IsEnabled, cancellationToken));
    }
    catch (Exception exception) when (exception is CatalogValidationException or CatalogConflictException or
                                      CatalogNotFoundException or UnauthorizedAccessException)
    {
        return CatalogError(exception);
    }
}).RequireAuthorization("Administrator");
app.MapDelete("/api/v1/admin/contexts/{contextId}", async (string contextId, long version, HttpContext context,
    ClaimsPrincipal principal, UserManager<ApplicationUser> users, ICameraCatalog catalog,
    CancellationToken cancellationToken) =>
{
    if (!IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var actor = await users.GetUserAsync(principal);
    if (actor is null) return Results.Unauthorized();
    try
    {
        return Results.Ok(await catalog.DeactivateContextAsync(actor.Id, contextId, version, cancellationToken));
    }
    catch (Exception exception) when (exception is CatalogValidationException or CatalogConflictException or
                                      CatalogNotFoundException or UnauthorizedAccessException)
    {
        return CatalogError(exception);
    }
}).RequireAuthorization("Administrator");
app.MapGet("/api/v1/admin/cameras", async (int? page, int? pageSize, string? contextId, ICameraCatalog catalog,
    CancellationToken cancellationToken) => Results.Ok(await catalog.ListCamerasAsync(page ?? 1, pageSize ?? 50,
    contextId, cancellationToken))).RequireAuthorization("Administrator");
app.MapPost("/api/v1/admin/cameras", async (CreateCameraCatalogRequest request, HttpContext context,
    ClaimsPrincipal principal, UserManager<ApplicationUser> users, ICameraCatalog catalog,
    CancellationToken cancellationToken) =>
{
    if (!IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var actor = await users.GetUserAsync(principal);
    if (actor is null) return Results.Unauthorized();
    try
    {
        var created = await catalog.CreateCameraAsync(actor.Id, request.CameraId, request.ContextId, request.Name,
            cancellationToken);
        return Results.Created($"/api/v1/admin/cameras/{created.CameraId}", created);
    }
    catch (Exception exception) when (exception is CatalogValidationException or CatalogConflictException or
                                      CatalogNotFoundException or UnauthorizedAccessException)
    {
        return CatalogError(exception);
    }
}).RequireAuthorization("Administrator");
app.MapPut("/api/v1/admin/cameras/{cameraId}", async (string cameraId, UpdateCameraCatalogRequest request,
    HttpContext context, ClaimsPrincipal principal, UserManager<ApplicationUser> users, ICameraCatalog catalog,
    CancellationToken cancellationToken) =>
{
    if (!IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var actor = await users.GetUserAsync(principal);
    if (actor is null) return Results.Unauthorized();
    try
    {
        return Results.Ok(await catalog.UpdateCameraAsync(actor.Id, cameraId, request.Version, request.ContextId,
            request.Name, request.IsEnabled, cancellationToken));
    }
    catch (Exception exception) when (exception is CatalogValidationException or CatalogConflictException or
                                      CatalogNotFoundException or UnauthorizedAccessException)
    {
        return CatalogError(exception);
    }
}).RequireAuthorization("Administrator");
app.MapDelete("/api/v1/admin/cameras/{cameraId}", async (string cameraId, long version, HttpContext context,
    ClaimsPrincipal principal, UserManager<ApplicationUser> users, ICameraCatalog catalog,
    CancellationToken cancellationToken) =>
{
    if (!IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var actor = await users.GetUserAsync(principal);
    if (actor is null) return Results.Unauthorized();
    try
    {
        return Results.Ok(await catalog.DeactivateCameraAsync(actor.Id, cameraId, version, cancellationToken));
    }
    catch (Exception exception) when (exception is CatalogValidationException or CatalogConflictException or
                                      CatalogNotFoundException or UnauthorizedAccessException)
    {
        return CatalogError(exception);
    }
}).RequireAuthorization("Administrator");
app.MapGet("/api/v1/admin/users", async (int? page, int? pageSize, UserManager<ApplicationUser> users,
    CancellationToken cancellationToken) =>
{
    var currentPage = page ?? 1;
    var currentPageSize = pageSize ?? 25;
    if (currentPage < 1 || currentPageSize is < 1 or > 100)
        return Results.BadRequest(new { code = "invalid_pagination" });
    var query = users.Users.AsNoTracking().OrderBy(user => user.NormalizedUserName).ThenBy(user => user.Id);
    var totalCount = await query.CountAsync(cancellationToken);
    var offset = ((long)currentPage - 1) * currentPageSize;
    var items = offset >= totalCount
        ? []
        : await query.Skip((int)offset).Take(currentPageSize)
            .Select(user => new AdminUserSummaryResponse(user.Id, user.UserName ?? string.Empty, user.IsActive))
            .ToArrayAsync(cancellationToken);
    return Results.Ok(new AdminUserPageResponse(items, currentPage, currentPageSize, totalCount));
}).RequireAuthorization("Administrator");
app.MapGet("/api/v1/admin/users/{userId:guid}", async (Guid userId, UserManager<ApplicationUser> users,
    IamDbContext database, CancellationToken cancellationToken) =>
{
    var user = await users.FindByIdAsync(userId.ToString());
    if (user is null) return Results.NotFound(new { code = "user_not_found" });
    var roles = (await users.GetRolesAsync(user)).Order(StringComparer.Ordinal).ToArray();
    var grants = await (from grant in database.CameraAccessGrants.AsNoTracking()
                        where grant.UserId == userId
                        join camera in database.CameraCatalog.AsNoTracking()
                            on grant.CameraId equals camera.CameraId into cameras
                        from camera in cameras.DefaultIfEmpty()
                        orderby grant.GrantedAtUtc descending, grant.Id
                        select new AdminUserCameraGrantResponse(grant.ContextId, grant.CameraId,
                            camera == null ? null : camera.Name, grant.GrantedAtUtc, grant.RevokedAtUtc))
        .ToArrayAsync(cancellationToken);
    return Results.Ok(new AdminUserDetailsResponse(user.Id, user.UserName ?? string.Empty, user.IsActive,
        roles, grants));
}).RequireAuthorization("Administrator");
app.MapPut("/api/v1/admin/users/{userId:guid}/roles", async (Guid userId, SetUserRolesRequest request,
    HttpContext context, ClaimsPrincipal principal, UserManager<ApplicationUser> users,
    IdentityAdministrationService administration, CancellationToken cancellationToken) =>
{
    if (!IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var actor = await users.GetUserAsync(principal);
    if (actor is null) return Results.Unauthorized();
    if (request.Roles is null) return Results.BadRequest(new { code = "invalid_admin_request" });
    try
    {
        await administration.SetRolesAsync(actor.Id, userId, request.Roles, cancellationToken);
        return Results.NoContent();
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                      KeyNotFoundException or UnauthorizedAccessException)
    {
        return AdminIdentityError(exception);
    }
}).RequireAuthorization("Administrator");
app.MapPut("/api/v1/admin/users/{userId:guid}/cameras/{cameraId}", async (Guid userId, string cameraId,
    HttpContext context, ClaimsPrincipal principal, UserManager<ApplicationUser> users, IamDbContext database,
    IdentityAdministrationService administration, CancellationToken cancellationToken) =>
{
    if (!IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var actor = await users.GetUserAsync(principal);
    if (actor is null) return Results.Unauthorized();
    var contextId = await database.CameraCatalog.AsNoTracking()
        .Where(camera => camera.CameraId == cameraId && camera.IsEnabled)
        .Select(camera => camera.ContextId)
        .SingleOrDefaultAsync(cancellationToken);
    if (contextId is null) return Results.NotFound(new { code = "camera_not_found" });
    try
    {
        await administration.GrantCameraAsync(actor.Id, userId, contextId, cameraId, cancellationToken);
        return Results.NoContent();
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                      KeyNotFoundException or UnauthorizedAccessException)
    {
        return AdminIdentityError(exception);
    }
}).RequireAuthorization("Administrator");
app.MapDelete("/api/v1/admin/users/{userId:guid}/cameras/{cameraId}", async (Guid userId, string cameraId,
    HttpContext context, ClaimsPrincipal principal, UserManager<ApplicationUser> users,
    IdentityAdministrationService administration, CancellationToken cancellationToken) =>
{
    if (!IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var actor = await users.GetUserAsync(principal);
    if (actor is null) return Results.Unauthorized();
    try
    {
        await administration.RevokeCameraAsync(actor.Id, userId, cameraId, cancellationToken);
        return Results.NoContent();
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                      KeyNotFoundException or UnauthorizedAccessException)
    {
        return AdminIdentityError(exception);
    }
}).RequireAuthorization("Administrator");
app.MapDelete("/api/v1/admin/users/{userId:guid}", async (Guid userId, HttpContext context,
    ClaimsPrincipal principal, UserManager<ApplicationUser> users, IdentityAdministrationService administration,
    CancellationToken cancellationToken) =>
{
    if (!IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    var actor = await users.GetUserAsync(principal);
    if (actor is null) return Results.Unauthorized();
    try
    {
        await administration.DeactivateUserAsync(actor.Id, userId, cancellationToken);
        return Results.NoContent();
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                      KeyNotFoundException or UnauthorizedAccessException)
    {
        return AdminIdentityError(exception);
    }
}).RequireAuthorization("Administrator");
app.MapGet("/api/v1/live/{cameraId}/index.m3u8", async (string cameraId,
    ClaimsPrincipal principal, UserManager<ApplicationUser> users, ICameraResourceAccess cameraAccess,
    GarageWatch.Infrastructure.Capture.CaptureOptions captureOptions,
    GarageWatch.Infrastructure.Capture.LocalSegmentSpool spool,
    LivePlaylistSequence playlistSequence, CancellationToken cancellationToken) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null || !user.IsActive) return Results.Unauthorized();
    if (!await cameraAccess.CanReadLiveAsync(user.Id, cameraId, cancellationToken)) return Results.NotFound();
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
app.MapGet("/api/v1/live/{cameraId}/segments/{segmentId}", async (string cameraId, string segmentId,
    ClaimsPrincipal principal, UserManager<ApplicationUser> users, ICameraResourceAccess cameraAccess,
    GarageWatch.Infrastructure.Capture.CaptureOptions captureOptions,
    GarageWatch.Infrastructure.Capture.LocalSegmentSpool spool, CancellationToken cancellationToken) =>
{
    var user = await users.GetUserAsync(principal);
    if (user is null || !user.IsActive) return Results.Unauthorized();
    if (!await cameraAccess.CanReadLiveAsync(user.Id, cameraId, cancellationToken)) return Results.NotFound();
    if (!string.Equals(cameraId, captureOptions.CameraId, StringComparison.Ordinal) ||
        !GarageWatch.Infrastructure.Capture.LocalSegmentSpool.IsSafeSegmentId(segmentId)) return Results.NotFound();
    var item = spool.GetRecentSegments(captureOptions.MaximumSegments).FirstOrDefault(x => x.Manifest.SegmentId == segmentId);
    return item.Manifest is null ? Results.NotFound() : Results.File(item.Path, "video/mp2t", enableRangeProcessing: true);
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

static bool IsSameOrigin(HttpContext context)
{
    var origin = context.Request.Headers["Origin"].ToString();
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var suppliedOrigin) ||
        !string.IsNullOrEmpty(suppliedOrigin.UserInfo) || suppliedOrigin.AbsolutePath != "/" ||
        !string.IsNullOrEmpty(suppliedOrigin.Query) || !string.IsNullOrEmpty(suppliedOrigin.Fragment))
        return false;

    var requestOriginText = $"{context.Request.Scheme}://{context.Request.Host}";
    return Uri.TryCreate(requestOriginText, UriKind.Absolute, out var requestOrigin) &&
        suppliedOrigin.IsLoopback && requestOrigin.IsLoopback &&
        string.Equals(suppliedOrigin.GetLeftPart(UriPartial.Authority),
            requestOrigin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
}

static IResult CatalogError(Exception exception) => exception switch
{
    CatalogValidationException => Results.BadRequest(new { code = "invalid_catalog_request" }),
    CatalogNotFoundException => Results.NotFound(new { code = "catalog_item_not_found" }),
    CatalogConflictException => Results.Conflict(new { code = "catalog_conflict" }),
    UnauthorizedAccessException => Results.Forbid(),
    _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
};

static IResult AdminIdentityError(Exception exception) => exception switch
{
    ArgumentException => Results.BadRequest(new { code = "invalid_admin_request" }),
    KeyNotFoundException => Results.NotFound(new { code = "admin_resource_not_found" }),
    InvalidOperationException => Results.Conflict(new { code = "admin_operation_conflict" }),
    UnauthorizedAccessException => Results.Forbid(),
    _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
};

app.Run();

public partial class Program { }
