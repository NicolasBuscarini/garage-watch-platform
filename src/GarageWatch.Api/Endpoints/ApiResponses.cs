using GarageWatch.Api.Contracts;

namespace GarageWatch.Api.Endpoints;

public sealed record ApiErrorResponse(string Code);

public sealed record LivenessResponse(string Status);

public sealed record SpoolReadinessResponse(string Status, long Segments, int SegmentLimit);

public sealed record CaptureReadinessResponse(string Adapter, string AdapterVersion,
    IReadOnlyList<string> Capabilities, string State, string? StreamEpoch, long Gaps, long Segments,
    string? LastErrorCode, SpoolReadinessResponse Spool);

public sealed record ReadinessResponse(string Status, CaptureReadinessResponse Capture);

public sealed record SessionResponse(bool Authenticated);

public sealed record PresenceResponse(int ActiveClients, double LeaseSeconds);

public sealed record PresenceCapacityErrorResponse(string Code, int ActiveClients);

public sealed record CameraSummaryResponse(string CameraId, string ContextId, string Name, string Status,
    string? StreamEpoch, string LiveUrl);
