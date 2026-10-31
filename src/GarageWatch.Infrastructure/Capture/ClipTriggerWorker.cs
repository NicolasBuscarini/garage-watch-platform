using System.Threading.Channels;
using System.Text.Json;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Capture;

public sealed class ClipTriggerWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Meter Meter = new("GarageWatch.ClipTrigger", "1.0.0");
    private static readonly Counter<long> Observations = Meter.CreateCounter<long>("garagewatch.clip_trigger.observations");
    private static readonly Counter<long> Clips = Meter.CreateCounter<long>("garagewatch.clips");
    private readonly Channel<SegmentManifestV1> queue;
    private readonly List<PendingClip> pending = [];
    private DateTimeOffset lastTriggerAt = DateTimeOffset.MinValue;

    public ClipTriggerWorker(CaptureOptions capture, ClipTriggerOptions options, LocalSegmentSpool spool,
        IVisualChangeDetector detector, IClipMediaAssembler assembler, ILogger<ClipTriggerWorker> logger)
    {
        Capture = capture;
        Options = options;
        Spool = spool;
        Detector = detector;
        Assembler = assembler;
        Logger = logger;
        queue = Channel.CreateBounded<SegmentManifestV1>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    }

    private CaptureOptions Capture { get; }
    private ClipTriggerOptions Options { get; }
    private LocalSegmentSpool Spool { get; }
    private IVisualChangeDetector Detector { get; }
    private IClipMediaAssembler Assembler { get; }
    private ILogger<ClipTriggerWorker> Logger { get; }

    public bool TryEnqueue(SegmentManifestV1 segment)
    {
        var accepted = Options.Enabled && queue.Writer.TryWrite(segment);
        Observations.Add(1, new TagList { { "outcome", accepted ? "accepted" : "dropped" } });
        return accepted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Options.Enabled) return;
        await RecoverPendingAsync(stoppingToken);
        await foreach (var segment in queue.Reader.ReadAllAsync(stoppingToken))
        {
            await AdvancePendingAsync(segment, stoppingToken);
            var path = Spool.TryGetSegmentPath(segment);
            if (path is null || DateTimeOffset.UtcNow - lastTriggerAt < TimeSpan.FromSeconds(Options.CooldownSeconds)) continue;
            if (!await Detector.HasRelevantChangeAsync(path, stoppingToken))
            {
                Observations.Add(1, new TagList { { "outcome", "no_change" } });
                continue;
            }
            Observations.Add(1, new TagList { { "outcome", "triggered" } });
            await StartClipAsync(segment, stoppingToken);
        }
    }

    private async Task RecoverPendingAsync(CancellationToken cancellationToken)
    {
        foreach (var clip in ReadManifests().Where(clip => clip.State == "collecting"))
        {
            var known = Spool.GetRecentSegments(10_000)
                .Where(item => clip.SegmentIds.Contains(item.Manifest.SegmentId, StringComparer.Ordinal)).ToList();
            if (known.Count == 0)
            {
                await PersistAsync(clip with { State = "lost_capacity", UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
                Clips.Add(1, new TagList { { "state", "lost_capacity" } });
                continue;
            }
            var pendingClip = new PendingClip(clip, clip.RequestedEndedAt);
            pending.Add(pendingClip);
            var newest = Spool.GetRecentSegments(1).SingleOrDefault();
            if (newest.Manifest is not null && newest.Manifest.EndedAt >= pendingClip.Deadline)
                await CompleteAsync(pendingClip, cancellationToken);
        }
    }

    private async Task StartClipAsync(SegmentManifestV1 segment, CancellationToken cancellationToken)
    {
        var triggerReason = $"visual_change:{segment.SegmentId}";
        if (ReadManifests().Any(clip => clip.TriggerReason == triggerReason)) return;
        var now = DateTimeOffset.UtcNow;
        var preStart = segment.EndedAt - TimeSpan.FromSeconds(Options.PreEventSeconds);
        var selected = Spool.GetRecentSegments(10_000)
            .Where(item => item.Manifest.EndedAt >= preStart).Select(item => item.Manifest).ToArray();
        if (selected.Length == 0) return;
        var id = Guid.NewGuid().ToString("N");
        var clip = new ClipManifestV1("1.0", id, Capture.CameraId, preStart,
            segment.EndedAt + TimeSpan.FromSeconds(Options.PostEventSeconds), selected.First().StartedAt, segment.EndedAt,
            "collecting", "trigger-v1", triggerReason, selected.Select(item => item.SegmentId).ToArray(), null, null, null, null, 0, now);
        var item = new PendingClip(clip, clip.RequestedEndedAt);
        pending.Add(item);
        lastTriggerAt = now;
        await PersistAsync(clip, cancellationToken);
        Logger.LogInformation("Clipe seletivo iniciado: {ClipId}", clip.ClipId);
        if (Options.PostEventSeconds == 0) await CompleteAsync(item, cancellationToken);
    }

    private async Task AdvancePendingAsync(SegmentManifestV1 segment, CancellationToken cancellationToken)
    {
        foreach (var item in pending.ToArray())
        {
            var clip = item.Clip;
            if (!clip.SegmentIds.Contains(segment.SegmentId, StringComparer.Ordinal))
                clip = clip with
                {
                    SegmentIds = clip.SegmentIds.Append(segment.SegmentId).ToArray(),
                    EffectiveEndedAt = segment.EndedAt,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            item.Clip = clip;
            await PersistAsync(clip, cancellationToken);
            if (segment.EndedAt >= item.Deadline) await CompleteAsync(item, cancellationToken);
        }
    }

    private async Task CompleteAsync(PendingClip item, CancellationToken cancellationToken)
    {
        var clip = item.Clip;
        try
        {
            var paths = Spool.GetRecentSegments(10_000)
                .Where(item => clip.SegmentIds.Contains(item.Manifest.SegmentId, StringComparer.Ordinal))
                .OrderBy(item => item.Manifest.StartedAt).Select(item => item.Path).ToArray();
            if (paths.Length != clip.SegmentIds.Count) throw new InvalidDataException("clip_segments_unavailable");
            var media = await Assembler.AssembleAsync(clip, paths, cancellationToken);
            await PersistAsync(clip with
            {
                State = "ready",
                MediaFileName = media.MediaFileName,
                MediaSha256 = media.MediaSha256,
                ThumbnailFileName = media.ThumbnailFileName,
                ThumbnailSha256 = media.ThumbnailSha256,
                AttemptCount = clip.AttemptCount + 1,
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken);
            Clips.Add(1, new TagList { { "state", "ready" } });
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            await PersistAsync(clip with { State = "failed", AttemptCount = clip.AttemptCount + 1, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken);
            Clips.Add(1, new TagList { { "state", "failed" } });
            Logger.LogWarning("Montagem de clipe falhou; motivo={FailureType}", exception.GetType().Name);
        }
        finally { pending.Remove(item); }
    }

    private IEnumerable<ClipManifestV1> ReadManifests()
    {
        var directory = ManifestDirectory;
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.json").Select(path =>
        {
            try { return JsonSerializer.Deserialize<ClipManifestV1>(File.ReadAllText(path), JsonOptions); }
            catch (JsonException) { return null; }
        }).Where(clip => clip is not null && clip.SchemaVersion == "1.0" && clip.CameraId == Capture.CameraId &&
            LocalSegmentSpool.IsSafeSegmentId(clip.ClipId)).Cast<ClipManifestV1>().ToArray();
    }

    private string ManifestDirectory => Path.Combine(Capture.SpoolDirectory, "clips", "manifests");
    private async Task PersistAsync(ClipManifestV1 clip, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ManifestDirectory);
        var path = Path.Combine(ManifestDirectory, clip.ClipId + ".json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(clip, JsonOptions), cancellationToken);
        File.Move(temporary, path, true);
    }

    private sealed class PendingClip(ClipManifestV1 clip, DateTimeOffset deadline)
    {
        public ClipManifestV1 Clip { get; set; } = clip;
        public DateTimeOffset Deadline { get; } = deadline;
    }
}
