using GarageWatch.Api.Errors;

namespace GarageWatch.Api.Endpoints;

public abstract class BaseEndpoint(string domain, string operationId, string summary, string description)
{
    public string Domain { get; } = domain;
    public string OperationId { get; } = operationId;
    public string Summary { get; } = summary;
    public string Description { get; } = description;

    public RouteHandlerBuilder Map(IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return MapCore(routes)
            .Produces<ErrorResponse>(StatusCodes.Status500InternalServerError)
            .WithTags(Domain)
            .WithName(OperationId)
            .WithSummary(Summary)
            .WithDescription(Description);
    }

    protected abstract RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes);
}
