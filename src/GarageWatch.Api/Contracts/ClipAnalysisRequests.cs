namespace GarageWatch.Api.Contracts;

public sealed record UpdateClipAnalysisDispatchPolicyRequest(long ExpectedVersion, string Policy);
public sealed record CreateClipAnalysisRequest(Guid IdempotencyKey, string? AnalysisVersion);
