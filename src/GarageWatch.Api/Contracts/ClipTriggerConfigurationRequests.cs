namespace GarageWatch.Api.Contracts;

public sealed record UpdateClipTriggerConfigurationRequest(long ExpectedVersion, bool Enabled);
