using GarageWatch.Api.Correlation;
using GarageWatch.Api.Endpoints;
using GarageWatch.Api.Contracts;
using GarageWatch.Application.Profiles;
using MediatR;

namespace GarageWatch.Api.Endpoints.System;

public sealed class GetSystemProfileEndpoint() : BaseEndpoint("System", "GetSystemProfile",
    "Get sanitized service profile", "Returns the versioned, non-sensitive host profile with its validated correlation ID. Administrator access is required.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/system-profile", async (IMediator mediator, ICorrelationContext correlation,
            CancellationToken cancellationToken) =>
        {
            var profile = await mediator.Send(new GetSystemProfileQuery(), cancellationToken);
            return Results.Ok(new SystemProfileResponseV1("1.0", correlation.CorrelationId,
                profile.ServiceName, profile.Environment, profile.ApplicationVersion));
        }).RequireAuthorization("Administrator")
            .Produces<SystemProfileResponseV1>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
