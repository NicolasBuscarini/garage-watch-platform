using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Application.Authorization;
using GarageWatch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Capture;

public sealed record ClipListItem(string ClipId, string CameraId, DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc, string State, string ArchiveStatus, string AnalysisStatus, string? ThumbnailUrl,
    long? SizeBytes, string? TriggerReason, string? TriggerConfigVersion);
public sealed record ClipPage(IReadOnlyList<ClipListItem> Items, int Page, int PageSize, int Total);
public sealed record ClipDetails(ClipListItem Clip, string? MediaUrl, string? MediaSha256,
    string? ThumbnailSha256, IReadOnlyList<string> SegmentIds, int AttemptCount,
    DateTimeOffset UpdatedAtUtc);

public sealed class ClipLibraryService(CaptureOptions capture, IamDbContext database, AccessProfileService profiles)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ClipPage> ListAsync(Guid userId, bool isAdministrator, string? cameraId,
        DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? state, int page, int pageSize,
        CancellationToken cancellationToken)
    {
        if (page is < 1 or > 100_000 || pageSize is < 1 or > 100 ||
            (fromUtc.HasValue && toUtc.HasValue && fromUtc > toUtc) ||
            (state is not null && state is not ("collecting" or "ready" or "failed" or "lost_capacity")))
            throw new ArgumentException("invalid_clip_query");

        var allowedCameras = await database.CameraCatalog.AsNoTracking()
            .Where(camera => camera.IsEnabled && camera.Context.IsEnabled &&
                (cameraId == null || camera.CameraId == cameraId))
            .Select(camera => new { camera.CameraId, camera.ContextId }).ToArrayAsync(cancellationToken);
        var accessible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var camera in allowedCameras)
            if (isAdministrator || await profiles.IsAllowedAsync(userId,
                    new PermissionRequest("clips.read", camera.ContextId, camera.CameraId), cancellationToken))
                accessible.Add(camera.CameraId);
        if (cameraId is not null && !accessible.Contains(cameraId)) return new([], page, pageSize, 0);

        var directory = ManifestDirectory;
        if (!Directory.Exists(directory)) return new([], page, pageSize, 0);
        var clips = new List<ClipManifestV1>();
        var manifestPaths = Directory.EnumerateFiles(directory, "*.json")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .Take(10_000)
            .Select(file => file.FullName);
        foreach (var path in manifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clip = await ReadManifestAsync(path, cancellationToken);
            if (clip is null || !accessible.Contains(clip.CameraId) ||
                (fromUtc.HasValue && clip.EffectiveEndedAt < fromUtc) ||
                (toUtc.HasValue && clip.EffectiveStartedAt > toUtc) || (state is not null && clip.State != state))
                continue;
            clips.Add(clip);
        }
        var ids = clips.Select(clip => clip.ClipId).ToArray();
        var latestJobs = new Dictionary<string, ClipAnalysisJob>(StringComparer.Ordinal);
        foreach (var chunk in ids.Chunk(500))
        {
            var jobs = await database.ClipAnalysisJobs.AsNoTracking().Where(job => chunk.Contains(job.ClipId))
                .OrderByDescending(job => job.CreatedAtUtc).ToArrayAsync(cancellationToken);
            foreach (var job in jobs)
                if (!latestJobs.TryGetValue(job.ClipId, out var current) || job.CreatedAtUtc > current.CreatedAtUtc)
                    latestJobs[job.ClipId] = job;
        }
        var ordered = clips.OrderByDescending(clip => clip.EffectiveStartedAt).ThenBy(clip => clip.ClipId,
            StringComparer.Ordinal).ToArray();
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(clip => Map(clip,
            latestJobs.GetValueOrDefault(clip.ClipId)?.State ?? "not_requested")).ToArray();
        return new(items, page, pageSize, ordered.Length);
    }

    public async Task<(ClipDetails? Details, string? MediaPath, string? ThumbnailPath)> GetAsync(Guid userId,
        bool isAdministrator, string clipId, CancellationToken cancellationToken)
    {
        if (!LocalSegmentSpool.IsSafeSegmentId(clipId)) return (null, null, null);
        var clip = await ReadManifestAsync(Path.Combine(ManifestDirectory, clipId + ".json"), cancellationToken);
        if (clip is null) return (null, null, null);
        var camera = await database.CameraCatalog.AsNoTracking().Where(item => item.CameraId == clip.CameraId &&
            item.IsEnabled && item.Context.IsEnabled).Select(item => new { item.CameraId, item.ContextId })
            .SingleOrDefaultAsync(cancellationToken);
        if (camera is null || (!isAdministrator && !await profiles.IsAllowedAsync(userId,
                new PermissionRequest("clips.read", camera.ContextId, camera.CameraId), cancellationToken)))
            return (null, null, null);

        var job = await database.ClipAnalysisJobs.AsNoTracking().Where(item => item.ClipId == clipId)
            .OrderByDescending(item => item.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);
        var item = Map(clip, job?.State ?? "not_requested");
        var mediaPath = ResolveMedia(clip);
        var thumbnailPath = ResolveThumbnail(clip);
        return (new(item, mediaPath is null ? null : $"/api/v1/clips/{clipId}/media",
            clip.MediaSha256, clip.ThumbnailSha256, clip.SegmentIds, clip.AttemptCount, clip.UpdatedAt),
            mediaPath, thumbnailPath);
    }

    public static async Task<bool> HasValidHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit) || !File.Exists(path)) return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expected));
    }

    private async Task<ClipManifestV1?> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var clip = await JsonSerializer.DeserializeAsync<ClipManifestV1>(stream, JsonOptions, cancellationToken);
            if (clip is not { SchemaVersion: "1.0" } || !LocalSegmentSpool.IsSafeSegmentId(clip.ClipId) ||
                Path.GetFileName(path) != clip.ClipId + ".json" ||
                clip.State is not ("collecting" or "ready" or "failed" or "lost_capacity")) return null;
            return clip;
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private ClipListItem Map(ClipManifestV1 clip, string analysisStatus)
    {
        var media = ResolveMedia(clip);
        var thumbnail = ResolveThumbnail(clip);
        long? size = media is null ? null : new FileInfo(media).Length;
        return new(clip.ClipId, clip.CameraId, clip.EffectiveStartedAt, clip.EffectiveEndedAt, clip.State,
            clip.ArchiveStatus, analysisStatus, thumbnail is null ? null : $"/api/v1/clips/{clip.ClipId}/thumbnail",
            size, clip.TriggerReason, clip.TriggerConfigVersion);
    }

    private string? ResolveMedia(ClipManifestV1 clip) => Resolve("media", clip.MediaFileName, clip.MediaSha256);
    private string? ResolveThumbnail(ClipManifestV1 clip) => Resolve("thumbnails", clip.ThumbnailFileName, clip.ThumbnailSha256);
    private string? Resolve(string kind, string? filename, string? hash)
    {
        if (string.IsNullOrWhiteSpace(filename) || Path.GetFileName(filename) != filename ||
            string.IsNullOrWhiteSpace(hash) || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return null;
        var path = Path.Combine(capture.SpoolDirectory, "clips", kind, filename);
        return File.Exists(path) ? path : null;
    }
    private string ManifestDirectory => Path.Combine(capture.SpoolDirectory, "clips", "manifests");
}
