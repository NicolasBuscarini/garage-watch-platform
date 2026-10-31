using System.Security.Cryptography;
using System.Text;
using GarageWatch.Application.Analysis;

namespace GarageWatch.Application.Events;

public sealed class VersionedEventRuleEngine
{
    private const int MaximumTrackRuleStates = 8_192;
    private const double LineSideEpsilon = 0.002;

    private sealed class TrackRuleState
    {
        public DateTimeOffset LastSeenAt { get; set; }
        public NormalizedPoint? PreviousPosition { get; set; }
        public DateTimeOffset? ZoneEnteredAt { get; set; }
        public DateTimeOffset? LastEventAt { get; set; }
        public bool DwellEmitted { get; set; }
    }

    private readonly Dictionary<(string Camera, string Epoch, string Config, string Rule, string Track), TrackRuleState> _states = [];
    private readonly Dictionary<(string Camera, string Epoch), (string ConfigVersion, DateTimeOffset CapturedAt)> _lastFrames = [];

    public EventRuleEngineResult Process(DetectionResultV1 frame, EventRuleSetV1 ruleSet)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(ruleSet);
        var validationError = EventRuleSetValidator.Validate(ruleSet);
        if (validationError is not null) throw new ArgumentException(validationError, nameof(ruleSet));
        if (frame.SchemaVersion != "1.0" || frame.CameraId != ruleSet.CameraId || string.IsNullOrWhiteSpace(frame.StreamEpoch))
            throw new ArgumentException("event_frame_scope_invalid", nameof(frame));

        var cameraEpoch = (frame.CameraId, frame.StreamEpoch);
        if (_lastFrames.TryGetValue(cameraEpoch, out var previousFrame)
            && previousFrame.ConfigVersion == ruleSet.ConfigVersion && frame.CapturedAt <= previousFrame.CapturedAt)
            return new EventRuleEngineResult([], 0);
        foreach (var oldEpoch in _lastFrames.Keys.Where(key => key.Camera == frame.CameraId
                     && key.Epoch != frame.StreamEpoch).ToArray())
        {
            _lastFrames.Remove(oldEpoch);
            foreach (var stateKey in _states.Keys.Where(key => key.Camera == frame.CameraId && key.Epoch == oldEpoch.Epoch).ToArray())
                _states.Remove(stateKey);
        }

        _lastFrames[cameraEpoch] = (ruleSet.ConfigVersion, frame.CapturedAt);
        var events = new List<EventOccurrenceV1>();
        var dropped = 0;
        foreach (var rule in ruleSet.Rules)
        {
            foreach (var detection in frame.Detections)
            {
                if (!string.Equals(detection.ClassName, rule.TargetClass, StringComparison.Ordinal)
                    || detection.Score < rule.MinimumScore) continue;
                var stateKey = (frame.CameraId, frame.StreamEpoch, ruleSet.ConfigVersion, rule.RuleId, detection.TrackId);
                if (!_states.TryGetValue(stateKey, out var state))
                {
                    if (_states.Count >= MaximumTrackRuleStates)
                    {
                        dropped++;
                        continue;
                    }
                    _states[stateKey] = state = new TrackRuleState();
                }

                var position = Center(detection.Box);
                var gapExceeded = state.LastSeenAt != default
                    && (frame.CapturedAt - state.LastSeenAt).TotalSeconds > rule.MaximumObservationGapSeconds;
                if (gapExceeded)
                {
                    state.PreviousPosition = null;
                    state.ZoneEnteredAt = null;
                    state.DwellEmitted = false;
                }

                if (rule.Kind == EventRuleKind.ZoneDwell)
                    ProcessZoneRule(frame, ruleSet, rule, detection, position, state, events);
                else
                    ProcessLineRule(frame, ruleSet, rule, detection, position, state, events);
                state.LastSeenAt = frame.CapturedAt;
                state.PreviousPosition = position;
            }
        }

        PruneExpired(frame.CapturedAt);
        return new EventRuleEngineResult(events, dropped);
    }

    private static void ProcessZoneRule(DetectionResultV1 frame, EventRuleSetV1 ruleSet, EventRuleV1 rule,
        TrackedObject detection, NormalizedPoint position, TrackRuleState state, List<EventOccurrenceV1> events)
    {
        var inside = IsInsidePolygon(position, rule.ZonePolygon!);
        if (!inside)
        {
            state.ZoneEnteredAt = null;
            state.DwellEmitted = false;
            return;
        }
        state.ZoneEnteredAt ??= frame.CapturedAt;
        if (state.DwellEmitted || (frame.CapturedAt - state.ZoneEnteredAt.Value).TotalSeconds < rule.MinimumDwellSeconds)
            return;

        state.DwellEmitted = true;
        if (!IsCooldownElapsed(state.LastEventAt, frame.CapturedAt, rule.CooldownSeconds)) return;
        state.LastEventAt = frame.CapturedAt;
        var duration = (frame.CapturedAt - state.ZoneEnteredAt.Value).TotalSeconds;
        events.Add(CreateEvent(frame, ruleSet, rule, detection, position, "zone.dwell.threshold_reached",
            state.ZoneEnteredAt.Value, frame.CapturedAt, duration, null));
    }

    private static void ProcessLineRule(DetectionResultV1 frame, EventRuleSetV1 ruleSet, EventRuleV1 rule,
        TrackedObject detection, NormalizedPoint position, TrackRuleState state, List<EventOccurrenceV1> events)
    {
        if (state.PreviousPosition is not { } previous) return;
        var previousSide = SignedSide(rule.LineStart!, rule.LineEnd!, previous);
        var currentSide = SignedSide(rule.LineStart!, rule.LineEnd!, position);
        var positiveToNegative = previousSide > LineSideEpsilon && currentSide < -LineSideEpsilon;
        var negativeToPositive = previousSide < -LineSideEpsilon && currentSide > LineSideEpsilon;
        if (!(positiveToNegative && rule.Direction is EventCrossingDirection.Both or EventCrossingDirection.PositiveToNegative)
            && !(negativeToPositive && rule.Direction is EventCrossingDirection.Both or EventCrossingDirection.NegativeToPositive))
            return;
        if (!IsCooldownElapsed(state.LastEventAt, frame.CapturedAt, rule.CooldownSeconds)) return;
        state.LastEventAt = frame.CapturedAt;
        var direction = positiveToNegative ? "positive_to_negative" : "negative_to_positive";
        events.Add(CreateEvent(frame, ruleSet, rule, detection, position, "line.crossing", frame.CapturedAt,
            frame.CapturedAt, null, direction));
    }

    private static EventOccurrenceV1 CreateEvent(DetectionResultV1 frame, EventRuleSetV1 ruleSet, EventRuleV1 rule,
        TrackedObject detection, NormalizedPoint position, string reason, DateTimeOffset startedAt,
        DateTimeOffset endedAt, double? observedDuration, string? crossingDirection)
    {
        var eventId = CreateDeterministicId(string.Join('|', frame.CameraId, frame.StreamEpoch, ruleSet.ConfigVersion,
            rule.RuleId, rule.RuleVersion, detection.TrackId, reason, startedAt.ToUniversalTime().Ticks,
            endedAt.ToUniversalTime().Ticks));
        return new EventOccurrenceV1("1.0", eventId.ToString("N"), ruleSet.ContextId, frame.CameraId, frame.StreamEpoch,
            [detection.TrackId], rule.EventType, rule.AlertLevel, ruleSet.ConfigVersion, startedAt, endedAt,
            rule.RuleId, rule.RuleVersion,
            new EventEvidenceV1(reason, observedDuration, position, detection.Box, detection.Score, crossingDirection), endedAt);
    }

    private static Guid CreateDeterministicId(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        if (_states.Count < MaximumTrackRuleStates * 3 / 4) return;
        foreach (var key in _states.Where(pair => (now - pair.Value.LastSeenAt).TotalSeconds > 3_600)
                     .Select(pair => pair.Key).ToArray()) _states.Remove(key);
    }

    private static bool IsCooldownElapsed(DateTimeOffset? lastEvent, DateTimeOffset now, int cooldownSeconds) =>
        lastEvent is null || (now - lastEvent.Value).TotalSeconds >= cooldownSeconds;

    private static NormalizedPoint Center(DetectionBox box) => new((box.X1 + box.X2) / 2d, (box.Y1 + box.Y2) / 2d);

    private static double SignedSide(NormalizedPoint start, NormalizedPoint end, NormalizedPoint point) =>
        (end.X - start.X) * (point.Y - start.Y) - (end.Y - start.Y) * (point.X - start.X);

    private static bool IsInsidePolygon(NormalizedPoint point, IReadOnlyList<NormalizedPoint> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            var intersects = (a.Y > point.Y) != (b.Y > point.Y)
                && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (intersects) inside = !inside;
        }
        return inside;
    }
}
