namespace GarageWatch.Domain;

public sealed record SystemProfile(
    string ServiceName,
    string Environment,
    string ApplicationVersion);
