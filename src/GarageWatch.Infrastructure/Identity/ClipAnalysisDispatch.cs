namespace GarageWatch.Infrastructure.Identity;

public sealed class ClipAnalysisDispatchPolicy
{
    public string CameraId { get; set; } = string.Empty;
    public string ContextId { get; set; } = string.Empty;
    public string Policy { get; set; } = ClipAnalysisPolicies.ManualOnly;
    public long Version { get; set; }
    public string UpdatedByUserId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ClipAnalysisJob : IHasPublicId
{
    public Guid Id { get; set; }
    public string? PublicId { get; set; }
    public string ClipId { get; set; } = string.Empty;
    public string ContextId { get; set; } = string.Empty;
    public string CameraId { get; set; } = string.Empty;
    public Guid IdempotencyKey { get; set; }
    public string AnalysisVersion { get; set; } = string.Empty;
    public long PolicyVersion { get; set; }
    public string State { get; set; } = "analysis_pending";
    public string RequestKind { get; set; } = string.Empty;
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public static class ClipAnalysisPolicies
{
    public const string Always = "always";
    public const string LargeMotion = "large_motion";
    public const string ManualOnly = "manual_only";
    public const string Never = "never";

    public static bool IsValid(string value) => value is Always or LargeMotion or ManualOnly or Never;
}
