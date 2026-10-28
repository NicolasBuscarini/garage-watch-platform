namespace GarageWatch.Api.Contracts;

public sealed record SystemProfileResponseV1(
    string SchemaVersion,
    string CorrelationId,
    string ServiceName,
    string Environment,
    string ApplicationVersion);
