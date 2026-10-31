using GarageWatch.Application.Analysis;

namespace GarageWatch.Application.Events;

public enum EventRuleKind
{
    ZoneDwell,
    LineCrossing
}

public enum EventAlertLevel
{
    Informational,
    Attention,
    Critical
}

public enum EventCrossingDirection
{
    Both,
    PositiveToNegative,
    NegativeToPositive
}

public sealed record NormalizedPoint(double X, double Y);

public sealed record EventRuleV1(
    string RuleId,
    int RuleVersion,
    EventRuleKind Kind,
    string EventType,
    EventAlertLevel AlertLevel,
    int CooldownSeconds,
    int MaximumObservationGapSeconds,
    string TargetClass,
    float MinimumScore,
    IReadOnlyList<NormalizedPoint>? ZonePolygon = null,
    NormalizedPoint? LineStart = null,
    NormalizedPoint? LineEnd = null,
    EventCrossingDirection Direction = EventCrossingDirection.Both,
    double MinimumDwellSeconds = 0);

public sealed record EventRuleSetV1(
    string SchemaVersion,
    string ConfigVersion,
    string ContextId,
    string CameraId,
    IReadOnlyList<EventRuleV1> Rules);

public sealed record EventEvidenceV1(
    string ReasonCode,
    double? ObservedDurationSeconds,
    NormalizedPoint Position,
    DetectionBox BoundingBox,
    float DetectionScore,
    string? CrossingDirection);

public sealed record EventOccurrenceV1(
    string SchemaVersion,
    string EventId,
    string ContextId,
    string CameraId,
    string StreamEpoch,
    IReadOnlyList<string> TrackIds,
    string Type,
    EventAlertLevel AlertLevel,
    string RuleConfigVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string RuleId,
    int RuleVersion,
    EventEvidenceV1 Evidence,
    DateTimeOffset CreatedAt);

public sealed record EventRuleEngineResult(IReadOnlyList<EventOccurrenceV1> Events, int DroppedStateUpdates);

public static class EventRuleSetValidator
{
    public static string? Validate(EventRuleSetV1? ruleSet)
    {
        if (ruleSet is null) return null;
        if (ruleSet.SchemaVersion != "1.0" || !IsSafeToken(ruleSet.ConfigVersion, 64)
            || !IsSafeToken(ruleSet.ContextId, 64) || !IsSafeToken(ruleSet.CameraId, 64)
            || ruleSet.Rules is null || ruleSet.Rules.Count > 32) return "invalid_rule_set";
        if (ruleSet.Rules.Any(rule => rule is null)) return "invalid_rule";
        if (ruleSet.Rules.Select(rule => rule.RuleId).Distinct(StringComparer.Ordinal).Count() != ruleSet.Rules.Count)
            return "duplicate_rule_id";
        foreach (var rule in ruleSet.Rules)
        {
            if (!IsSafeToken(rule.RuleId, 64) || rule.RuleVersion < 1 || !IsSafeToken(rule.EventType, 64)
                || !IsSafeToken(rule.TargetClass, 64) || rule.CooldownSeconds is < 0 or > 86_400
                || rule.MaximumObservationGapSeconds is < 1 or > 3_600 || !float.IsFinite(rule.MinimumScore)
                || rule.MinimumScore is < 0 or > 1) return "invalid_rule";
            if (rule.Kind == EventRuleKind.ZoneDwell)
            {
                if (rule.ZonePolygon is not { Count: >= 3 and <= 64 } || !double.IsFinite(rule.MinimumDwellSeconds)
                    || rule.MinimumDwellSeconds is < 0 or > 86_400 || rule.LineStart is not null || rule.LineEnd is not null
                    || rule.ZonePolygon.Any(point => !IsNormalized(point))) return "invalid_zone_rule";
                var area = 0d;
                for (var i = 0; i < rule.ZonePolygon.Count; i++)
                {
                    var current = rule.ZonePolygon[i];
                    var next = rule.ZonePolygon[(i + 1) % rule.ZonePolygon.Count];
                    area += current.X * next.Y - next.X * current.Y;
                }
                if (Math.Abs(area) < 1e-9) return "degenerate_zone";
            }
            else if (rule.Kind == EventRuleKind.LineCrossing)
            {
                if (rule.LineStart is null || rule.LineEnd is null || !IsNormalized(rule.LineStart)
                    || !IsNormalized(rule.LineEnd) || rule.ZonePolygon is not null || rule.MinimumDwellSeconds != 0
                    || DistanceSquared(rule.LineStart, rule.LineEnd) < 1e-12) return "invalid_line_rule";
            }
            else return "unsupported_rule_kind";
        }
        return null;
    }

    private static bool IsSafeToken(string? value, int maximumLength) => value is { Length: > 0 } && value.Length <= maximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsNormalized(NormalizedPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y)
        && point.X is >= 0 and <= 1 && point.Y is >= 0 and <= 1;

    private static double DistanceSquared(NormalizedPoint left, NormalizedPoint right) =>
        Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2);
}
