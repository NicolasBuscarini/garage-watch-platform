namespace GarageWatch.Api.Errors;

public sealed record ErrorResponse(string Code, string CorrelationId);
