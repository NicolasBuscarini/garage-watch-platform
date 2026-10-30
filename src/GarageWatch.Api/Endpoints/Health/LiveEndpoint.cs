using GarageWatch.Api.Endpoints;

namespace GarageWatch.Api.Endpoints.Health;

public sealed class LiveEndpoint() : BaseEndpoint("Health", "GetLiveness", "Check process liveness",
    "Returns the minimal liveness state without probing camera, storage, or optional dependencies.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/health/live", () => Results.Ok(new LivenessResponse("live")))
            .AllowAnonymous().Produces<LivenessResponse>(StatusCodes.Status200OK);
}
