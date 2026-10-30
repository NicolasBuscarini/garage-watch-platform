using GarageWatch.Api.Endpoints;
using GarageWatch.Infrastructure.Capture;

namespace GarageWatch.Api.Endpoints.Health;

public sealed class ReadyEndpoint() : BaseEndpoint("Health", "GetReadiness", "Check capture readiness",
    "Reports sanitized capture and spool state. Requires an authenticated Administrator; returns 503 while capture is degraded.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/health/ready", (CaptureWorker capture, CaptureOptions options) =>
        {
            var status = capture.Status;
            var ready = status.State == "capturing";
            var response = new ReadinessResponse(ready ? "ready" : "degraded",
                new CaptureReadinessResponse(status.Adapter, status.AdapterVersion, status.Capabilities,
                    status.State, status.StreamEpoch, status.Gaps, status.Segments, status.LastErrorCode,
                    new SpoolReadinessResponse(status.State == "spool-unavailable" ? "unavailable" : "available",
                        status.Segments, options.MaximumSegments)));
            return Results.Json(response, statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).RequireAuthorization("Administrator")
            .Produces<ReadinessResponse>(StatusCodes.Status200OK)
            .Produces<ReadinessResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
