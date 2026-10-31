namespace GarageWatch.Api.Contracts;

public sealed record ClipListItemResponse(string ClipId, string CameraId, DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc, string State, string ArchiveStatus, string AnalysisStatus, string? ThumbnailUrl,
    long? SizeBytes, string? TriggerReason, string? TriggerConfigVersion);
public sealed record ClipPageResponse(IReadOnlyList<ClipListItemResponse> Items, int Page, int PageSize, int Total);
public sealed record ClipDetailsResponse(ClipListItemResponse Clip, string? MediaUrl, string? MediaSha256,
    string? ThumbnailSha256, IReadOnlyList<string> SegmentIds, int AttemptCount, DateTimeOffset UpdatedAtUtc);
