using GarageWatch.Application.Analysis;
using GarageWatch.Application.Events;
using GarageWatch.Infrastructure.Events;
using Xunit;

namespace GarageWatch.Api.Tests;

public sealed class EventRuleEngineTests
{
    [Fact]
    public void Zone_dwell_emits_once_at_threshold_and_replay_is_idempotent()
    {
        var engine = new VersionedEventRuleEngine();
        var rules = RuleSet(new EventRuleV1("dwell", 1, EventRuleKind.ZoneDwell, "person.in_zone",
            EventAlertLevel.Attention, 10, 60, "person", .5f, ZonePolygon: Square(), MinimumDwellSeconds: 30));
        Assert.Empty(engine.Process(Frame("f1", 0, new DetectionBox(.4f, .4f, .6f, .6f)), rules).Events);
        Assert.Empty(engine.Process(Frame("f2", 29, new DetectionBox(.4f, .4f, .6f, .6f)), rules).Events);

        var result = engine.Process(Frame("f3", 31, new DetectionBox(.4f, .4f, .6f, .6f)), rules);
        var occurrence = Assert.Single(result.Events);
        Assert.Equal("person.in_zone", occurrence.Type);
        Assert.Equal(31, occurrence.Evidence.ObservedDurationSeconds);
        Assert.Equal("zone.dwell.threshold_reached", occurrence.Evidence.ReasonCode);
        Assert.Empty(engine.Process(Frame("f3", 31, new DetectionBox(.4f, .4f, .6f, .6f)), rules).Events);
    }

    [Fact]
    public void Directed_line_crossing_honors_direction_and_cooldown()
    {
        var engine = new VersionedEventRuleEngine();
        var rules = RuleSet(new EventRuleV1("entry", 2, EventRuleKind.LineCrossing, "garage.entry",
            EventAlertLevel.Informational, 5, 5, "person", .5f, LineStart: new(.5, .1), LineEnd: new(.5, .9),
            Direction: EventCrossingDirection.PositiveToNegative));
        engine.Process(Frame("f1", 0, BoxAt(.3, .5)), rules);
        var crossing = Assert.Single(engine.Process(Frame("f2", 1, BoxAt(.7, .5)), rules).Events);
        Assert.Equal("positive_to_negative", crossing.Evidence.CrossingDirection);

        engine.Process(Frame("f3", 2, BoxAt(.3, .5)), rules);
        Assert.Empty(engine.Process(Frame("f4", 3, BoxAt(.7, .5)), rules).Events);
        engine.Process(Frame("f5", 6, BoxAt(.3, .5)), rules);
        Assert.Single(engine.Process(Frame("f6", 7, BoxAt(.7, .5)), rules).Events);
    }

    [Fact]
    public void Epoch_change_and_long_observation_gap_reset_temporal_state()
    {
        var engine = new VersionedEventRuleEngine();
        var rules = RuleSet(new EventRuleV1("dwell", 1, EventRuleKind.ZoneDwell, "person.in_zone",
            EventAlertLevel.Attention, 0, 60, "person", .5f, ZonePolygon: Square(), MinimumDwellSeconds: 30));
        engine.Process(Frame("f1", 0, BoxAt(.5, .5)), rules);
        Assert.Single(engine.Process(Frame("f2", 31, BoxAt(.5, .5)), rules).Events);
        engine.Process(Frame("f3", 32, BoxAt(.5, .5), epoch: "epoch-b"), rules);
        Assert.Single(engine.Process(Frame("f4", 63, BoxAt(.5, .5), epoch: "epoch-b"), rules).Events);
    }

    [Fact]
    public async Task Local_journal_is_checksum_validated_append_only_and_idempotent_after_recovery()
    {
        var root = Path.Combine(Path.GetTempPath(), $"garagewatch-events-{Guid.NewGuid():N}");
        var options = new EventEngineOptions(null, null, root, 2, 1_048_576);
        try
        {
            var occurrence = Assert.Single(new VersionedEventRuleEngine().Process(
                Frame("f1", 0, BoxAt(.5, .5)), RuleSet(new EventRuleV1("dwell", 1,
                    EventRuleKind.ZoneDwell, "person.in_zone", EventAlertLevel.Attention, 0, 5,
                    "person", .5f, ZonePolygon: Square(), MinimumDwellSeconds: 0))).Events);
            var journal = new LocalEventJournal(options);
            Assert.Equal(EventAppendStatus.Created, await journal.AppendAsync(occurrence, CancellationToken.None));
            Assert.Equal(EventAppendStatus.Duplicate, await journal.AppendAsync(occurrence, CancellationToken.None));

            var recoveredJournal = new LocalEventJournal(options);
            var recovery = await recoveredJournal.RecoverAsync(CancellationToken.None);
            Assert.Equal(1, recovery.RecoveredEvents);
            Assert.Equal(0, recovery.InvalidEvents);
            Assert.Equal(EventAppendStatus.Duplicate, await recoveredJournal.AppendAsync(occurrence, CancellationToken.None));
            var second = occurrence with { EventId = Guid.NewGuid().ToString("N") };
            Assert.Equal(EventAppendStatus.Created, await recoveredJournal.AppendAsync(second, CancellationToken.None));
            Assert.Equal(EventAppendStatus.CapacityReached,
                await recoveredJournal.AppendAsync(second with { EventId = Guid.NewGuid().ToString("N") }, CancellationToken.None));

            var storedPath = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).First();
            await File.AppendAllTextAsync(storedPath, "tamper");
            var afterTamper = await new LocalEventJournal(options).RecoverAsync(CancellationToken.None);
            Assert.Equal(1, afterTamper.InvalidEvents);
            Assert.Equal(1, afterTamper.RecoveredEvents);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Rules_reject_degenerate_geometry_and_out_of_range_coordinates()
    {
        var invalid = RuleSet(new EventRuleV1("bad", 1, EventRuleKind.ZoneDwell, "person.in_zone",
            EventAlertLevel.Attention, 0, 5, "person", .5f,
            ZonePolygon: [new(.1, .1), new(.2, .2), new(.3, .3)]));
        Assert.Equal("degenerate_zone", EventRuleSetValidator.Validate(invalid));
        Assert.Equal("invalid_zone_rule", EventRuleSetValidator.Validate(RuleSet(new EventRuleV1("bad", 1,
            EventRuleKind.ZoneDwell, "person.in_zone", EventAlertLevel.Attention, 0, 5, "person", .5f,
            ZonePolygon: [new(-.1, .1), new(.8, .1), new(.8, .8)]))));
    }

    private static EventRuleSetV1 RuleSet(EventRuleV1 rule) => new("1.0", "rules-v1", "garage", "camera-1", [rule]);
    private static IReadOnlyList<NormalizedPoint> Square() => [new(.1, .1), new(.9, .1), new(.9, .9), new(.1, .9)];
    private static DetectionBox BoxAt(double x, double y) => new((float)(x - .05), (float)(y - .05), (float)(x + .05), (float)(y + .05));

    private static DetectionResultV1 Frame(string id, int seconds, DetectionBox box, string epoch = "epoch-a") =>
        new("1.0", "camera-1", epoch, id, DateTimeOffset.UnixEpoch.AddSeconds(seconds),
            DateTimeOffset.UnixEpoch.AddSeconds(seconds), 100, 100, "fixture-model-v1",
            [new TrackedObject("camera-1:epoch:" + (epoch == "epoch-a" ? "1" : "2"), "person", 0, .9f, box)]);
}
