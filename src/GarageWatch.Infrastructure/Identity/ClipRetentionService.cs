using System.Text.Json;
using GarageWatch.Application.Identity;
using GarageWatch.Infrastructure.Capture;
using GarageWatch.Infrastructure.Identity.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GarageWatch.Infrastructure.Identity;

public sealed record ClipRetentionSettings(string CameraId, int RetentionDays, int? MaximumStorageGigabytes,
    long Version, DateTimeOffset? UpdatedAtUtc);

public sealed record ClipPreservationSettings(string ClipPublicId, bool IsPreserved, long Version);

public sealed record ClipRetentionWorkerSnapshot(int RetentionDays, long? MaximumStorageBytes,
    IReadOnlySet<string> PreservedClipIds, IReadOnlySet<string> ActiveClipIds);
public sealed record ClipRetentionEntry(string ClipId, string PublicId, string CameraId, DateTimeOffset StartedAt,
    DateTimeOffset EndedAt, long SizeBytes, string Directory);

public sealed class ClipRetentionConflictException(string code) : Exception(code);

public sealed class ClipRetentionService(IamDbContext database, IIamUnitOfWork unitOfWork,
    UserManager<ApplicationUser> users, TimeProvider timeProvider, CaptureOptions capture,
    ClipReadLeaseRegistry leases)
{
    private const int DefaultRetentionDays = 15;
    private const long BytesPerGiB = 1_073_741_824;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ClipRetentionSettings> GetSettingsAsync(string publicCameraId,
        CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(publicCameraId, PublicIdPrefixes.CameraPrefix)) throw new KeyNotFoundException();
        var camera = await database.CameraCatalog.AsNoTracking().Where(item => item.PublicId == publicCameraId)
            .Select(item => new { item.CameraId, item.PublicId }).SingleOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException();
        var policy = await database.ClipRetentionPolicies.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CameraId == camera.CameraId, cancellationToken);
        return Map(camera.PublicId!, policy);
    }

    public async Task<ClipRetentionSettings> UpdateSettingsAsync(Guid actorId, string publicCameraId,
        long expectedVersion, int retentionDays, int? maximumStorageGigabytes, CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(publicCameraId, PublicIdPrefixes.CameraPrefix) || expectedVersion < 0 ||
            retentionDays is < 1 or > 3650 || maximumStorageGigabytes is < 1 or > 1024)
            throw new ArgumentException("invalid_clip_retention_policy");

        return await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            await EnsureAdministratorAsync(actorId);
            var camera = await database.CameraCatalog.SingleOrDefaultAsync(item => item.PublicId == publicCameraId, token)
                ?? throw new KeyNotFoundException();
            var current = await database.ClipRetentionPolicies.SingleOrDefaultAsync(
                item => item.CameraId == camera.CameraId, token);
            var version = current?.Version ?? 0;
            if (version != expectedVersion) throw new ClipRetentionConflictException("clip_retention_version_conflict");

            long? maximumBytes = maximumStorageGigabytes is { } gigabytes
                ? checked(gigabytes * BytesPerGiB) : null;
            var currentDays = current?.RetentionDays ?? DefaultRetentionDays;
            var currentBytes = current?.MaximumStorageBytes;
            if (currentDays == retentionDays && currentBytes == maximumBytes)
                return Map(camera.PublicId!, current);

            var now = timeProvider.GetUtcNow();
            if (current is null)
            {
                current = new ClipRetentionPolicy
                {
                    CameraId = camera.CameraId,
                    RetentionDays = retentionDays,
                    MaximumStorageBytes = maximumBytes,
                    Version = 1,
                    UpdatedByUserId = actorId.ToString("D"),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                database.ClipRetentionPolicies.Add(current);
            }
            else
            {
                current.RetentionDays = retentionDays;
                current.MaximumStorageBytes = maximumBytes;
                current.Version = checked(current.Version + 1);
                current.UpdatedByUserId = actorId.ToString("D");
                current.UpdatedAtUtc = now;
            }
            database.IamAuditEvents.Add(new IamAuditEvent
            {
                Id = Guid.NewGuid(),
                ActorUserId = actorId,
                SubjectUserId = actorId,
                Action = "clip.retention.policy.updated",
                ResourceId = publicCameraId,
                OccurredAtUtc = now
            });
            return Map(camera.PublicId!, current);
        }, cancellationToken);
    }

    public async Task<ClipPreservationSettings> SetPreservationAsync(Guid actorId, string publicClipId,
        long expectedVersion, bool isPreserved, CancellationToken cancellationToken)
    {
        if (!PublicIdFormat.IsValid(publicClipId, PublicIdPrefixes.ClipPrefix) || expectedVersion < 0)
            throw new ArgumentException("invalid_clip_preservation");
        var manifest = await FindManifestAsync(publicClipId, cancellationToken) ?? throw new KeyNotFoundException();
        if (manifest.State != "ready" || manifest.ArchiveStatus != "archived")
            throw new ClipRetentionConflictException("clip_not_preservable");
        using var mutationLease = leases.TryAcquireMutation(publicClipId)
            ?? throw new ClipRetentionConflictException("clip_retention_in_progress");

        return await unitOfWork.ExecuteAtomicallyAsync(async token =>
        {
            await EnsureAdministratorAsync(actorId);
            var current = await database.ClipPreservations.SingleOrDefaultAsync(
                item => item.ClipId == manifest.ClipId, token);
            var version = current?.Version ?? 0;
            if (version != expectedVersion) throw new ClipRetentionConflictException("clip_preservation_version_conflict");
            if ((current?.IsPreserved ?? false) == isPreserved)
                return new ClipPreservationSettings(publicClipId, isPreserved, version);

            var now = timeProvider.GetUtcNow();
            if (current is null)
            {
                current = new ClipPreservation
                {
                    ClipId = manifest.ClipId,
                    PublicId = publicClipId,
                    CameraId = manifest.CameraId,
                    IsPreserved = isPreserved,
                    Version = 1,
                    UpdatedByUserId = actorId.ToString("D"),
                    UpdatedAtUtc = now
                };
                database.ClipPreservations.Add(current);
            }
            else
            {
                current.IsPreserved = isPreserved;
                current.Version = checked(current.Version + 1);
                current.UpdatedByUserId = actorId.ToString("D");
                current.UpdatedAtUtc = now;
            }
            database.IamAuditEvents.Add(new IamAuditEvent
            {
                Id = Guid.NewGuid(),
                ActorUserId = actorId,
                SubjectUserId = actorId,
                Action = isPreserved ? "clip.preservation.enabled" : "clip.preservation.disabled",
                ResourceId = publicClipId,
                OccurredAtUtc = now
            });
            return new ClipPreservationSettings(publicClipId, current.IsPreserved, current.Version);
        }, cancellationToken);
    }

    public async Task<ClipRetentionWorkerSnapshot> GetWorkerSnapshotAsync(string cameraId,
        CancellationToken cancellationToken)
    {
        var policy = await database.ClipRetentionPolicies.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CameraId == cameraId, cancellationToken);
        var preserved = await database.ClipPreservations.AsNoTracking()
            .Where(item => item.CameraId == cameraId && item.IsPreserved)
            .Select(item => item.ClipId).ToArrayAsync(cancellationToken);
        var active = await database.ClipAnalysisJobs.AsNoTracking()
            .Where(item => item.CameraId == cameraId && (item.State == "analysis_pending" || item.State == "processing"))
            .Select(item => item.ClipId).Distinct().ToArrayAsync(cancellationToken);
        return new(policy?.RetentionDays ?? DefaultRetentionDays, policy?.MaximumStorageBytes,
            preserved.ToHashSet(StringComparer.Ordinal), active.ToHashSet(StringComparer.Ordinal));
    }

    public async Task<bool> IsClipProtectedAsync(string clipId, CancellationToken cancellationToken)
    {
        return await database.ClipPreservations.AsNoTracking().AnyAsync(
            item => item.ClipId == clipId && item.IsPreserved, cancellationToken)
            || await database.ClipAnalysisJobs.AsNoTracking().AnyAsync(item => item.ClipId == clipId &&
                (item.State == "analysis_pending" || item.State == "processing"), cancellationToken);
    }

    public async Task RecordExpiredAsync(string publicClipId, CancellationToken cancellationToken)
    {
        await unitOfWork.ExecuteAtomicallyAsync(token =>
        {
            database.IamAuditEvents.Add(new IamAuditEvent
            {
                Id = Guid.NewGuid(),
                Action = "clip.retention.expired",
                ResourceId = publicClipId,
                OccurredAtUtc = timeProvider.GetUtcNow()
            });
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public static bool TryReadArchive(string directory, out ClipRetentionEntry? entry)
    {
        entry = null;
        try
        {
            var commitPath = Path.Combine(directory, "archive-manifest.json");
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(commitPath) & FileAttributes.ReparsePoint) != 0) return false;
            var manifest = JsonSerializer.Deserialize<ArchivedClipManifestV1>(File.ReadAllText(commitPath), JsonOptions);
            if (manifest is not { SchemaVersion: "1.0" } || !LocalSegmentSpool.IsSafeSegmentId(manifest.ClipId) ||
                !PublicIdFormat.IsValid(manifest.PublicId, PublicIdPrefixes.ClipPrefix) ||
                !LocalSegmentSpool.IsSafeSegmentId(Path.GetFileName(directory)) ||
                Path.GetFileName(directory) != manifest.ClipId || manifest.MediaSizeBytes < 0 ||
                manifest.ThumbnailSizeBytes < 0 || Path.GetFileName(manifest.MediaFileName) != manifest.MediaFileName ||
                Path.GetFileName(manifest.ThumbnailFileName) != manifest.ThumbnailFileName ||
                !IsHash(manifest.MediaSha256) || !IsHash(manifest.ThumbnailSha256)) return false;
            var mediaPath = Path.Combine(directory, manifest.MediaFileName);
            var thumbnailPath = Path.Combine(directory, manifest.ThumbnailFileName);
            if (!File.Exists(mediaPath) || !File.Exists(thumbnailPath) ||
                new FileInfo(mediaPath).Length != manifest.MediaSizeBytes ||
                new FileInfo(thumbnailPath).Length != manifest.ThumbnailSizeBytes ||
                (File.GetAttributes(mediaPath) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(thumbnailPath) & FileAttributes.ReparsePoint) != 0) return false;
            entry = new(manifest.ClipId, manifest.PublicId, manifest.CameraId, manifest.EffectiveStartedAt,
                manifest.EffectiveEndedAt,
                checked(manifest.MediaSizeBytes + manifest.ThumbnailSizeBytes), directory);
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private async Task EnsureAdministratorAsync(Guid actorId)
    {
        var actor = await users.FindByIdAsync(actorId.ToString());
        if (actor is null || !actor.IsActive || !await users.IsInRoleAsync(actor, "Administrator"))
            throw new UnauthorizedAccessException();
    }

    private async Task<ClipManifestV1?> FindManifestAsync(string publicClipId, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(capture.SpoolDirectory, "clips", "manifests");
        if (!Directory.Exists(directory)) return null;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Take(10_000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var clip = await JsonSerializer.DeserializeAsync<ClipManifestV1>(stream, JsonOptions, cancellationToken);
                if (clip is { SchemaVersion: "1.0" } && clip.PublicId == publicClipId &&
                    Path.GetFileName(path) == clip.ClipId + ".json" && LocalSegmentSpool.IsSafeSegmentId(clip.ClipId))
                    return clip;
            }
            catch (Exception exception) when (exception is IOException or JsonException) { }
        }
        return null;
    }

    private static ClipRetentionSettings Map(string publicCameraId, ClipRetentionPolicy? policy) =>
        new(publicCameraId, policy?.RetentionDays ?? DefaultRetentionDays,
            policy?.MaximumStorageBytes is { } bytes ? checked((int)(bytes / BytesPerGiB)) : null,
            policy?.Version ?? 0, policy?.UpdatedAtUtc);
}
