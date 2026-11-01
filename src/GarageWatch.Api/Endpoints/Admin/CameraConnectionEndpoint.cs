using System.Net;
using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Endpoints;
using GarageWatch.Application.Cameras;
using GarageWatch.Application.Identity;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class GetCameraConnectionEndpoint() : BaseEndpoint("Camera administration", "GetCameraConnection",
    "Get active camera connection", "Returns only the configured address for the single active capture camera; never returns credentials.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/cameras/{cameraId}/connection", async (string cameraId,
            CaptureOptions capture, IamDbContext database, ICameraConnectionManager manager,
            CancellationToken cancellationToken) =>
        {
            if (!PublicIdFormat.IsValid(cameraId, PublicIdPrefixes.CameraPrefix))
                return Results.BadRequest(new ApiErrorResponse("invalid_camera_id"));
            if (capture.Profile != "ffmpeg-rtsp")
                return Results.Conflict(new ApiErrorResponse("camera_capture_not_active"));
            var camera = await database.CameraCatalog.AsNoTracking()
                .Include(item => item.Context)
                .SingleOrDefaultAsync(item => item.PublicId == cameraId && item.CameraId == capture.CameraId
                    && item.IsEnabled && item.Context.IsEnabled, cancellationToken);
            if (camera is null) return Results.NotFound(new ApiErrorResponse("camera_not_found"));
            try
            {
                var address = await manager.GetAddressAsync(cancellationToken);
                return Results.Ok(new CameraConnectionResponse(camera.PublicId!, address.Address, true));
            }
            catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (HttpRequestException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        }).RequireAuthorization("Administrator")
            .Produces<CameraConnectionResponse>().Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound).Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status503ServiceUnavailable);
}

public sealed class UpdateCameraConnectionEndpoint() : BaseEndpoint("Camera administration", "UpdateCameraConnection",
    "Update active camera address", "Updates the local Neolink address; the camera reconnects using the existing protected credentials.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut("/api/v1/admin/cameras/{cameraId}/connection", async (string cameraId,
            UpdateCameraAddressRequest request, HttpContext context, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, CaptureOptions capture, IamDbContext database,
            ICameraConnectionManager manager, IIamUnitOfWork unitOfWork, IIamAuditEventRepository audit,
            TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!PublicIdFormat.IsValid(cameraId, PublicIdPrefixes.CameraPrefix))
                return Results.BadRequest(new ApiErrorResponse("invalid_camera_id"));
            if (!ValidHost(request.Address) || !ValidHost(request.ExpectedAddress))
                return Results.BadRequest(new ApiErrorResponse("invalid_camera_address"));
            if (capture.Profile != "ffmpeg-rtsp")
                return Results.Conflict(new ApiErrorResponse("camera_capture_not_active"));
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            var camera = await database.CameraCatalog.AsNoTracking()
                .Include(item => item.Context)
                .SingleOrDefaultAsync(item => item.PublicId == cameraId && item.CameraId == capture.CameraId
                    && item.IsEnabled && item.Context.IsEnabled, cancellationToken);
            if (camera is null) return Results.NotFound(new ApiErrorResponse("camera_not_found"));
            try
            {
                var updated = await manager.UpdateAddressAsync(request.ExpectedAddress, request.Address, cancellationToken);
                await unitOfWork.ExecuteAtomicallyAsync(token =>
                {
                    audit.Append(actor.Id, actor.Id, "camera.connection.address.updated", cameraId,
                        timeProvider.GetUtcNow());
                    return Task.CompletedTask;
                }, cancellationToken);
                return Results.Ok(new CameraConnectionResponse(cameraId, updated.Address, true));
            }
            catch (CameraAddressConflictException) { return Results.Conflict(new ApiErrorResponse("camera_address_conflict")); }
            catch (ArgumentException) { return Results.BadRequest(new ApiErrorResponse("invalid_camera_address")); }
            catch (InvalidOperationException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Conflict)
            { return Results.Conflict(new ApiErrorResponse("camera_address_conflict")); }
            catch (HttpRequestException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        }).RequireAuthorization("Administrator")
            .Produces<CameraConnectionResponse>().Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound).Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status503ServiceUnavailable);

    private static bool ValidHost(string host)
    {
        var ipv4 = global::System.Net.IPAddress.TryParse(host, out var ip)
            && ip.AddressFamily == global::System.Net.Sockets.AddressFamily.InterNetwork
            && IsPrivateIpv4(ip);
        var hostnameShape = host.Length is > 0 and <= 253 && host.Split('.').All(label => label.Length is > 0 and <= 63
            && char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1])
            && label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
        var hostname = hostnameShape && (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.'));
        return ipv4 || hostname;
    }

    private static bool IsPrivateIpv4(global::System.Net.IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] == 169 && bytes[1] == 254;
    }
}
