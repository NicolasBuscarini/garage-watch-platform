using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Infrastructure.Identity;

namespace GarageWatch.Infrastructure.Capture;

public sealed record ArchivedClipManifestV1(string SchemaVersion, string ClipId, string PublicId, string CameraId,
    DateTimeOffset RequestedStartedAt, DateTimeOffset RequestedEndedAt, DateTimeOffset EffectiveStartedAt,
    DateTimeOffset EffectiveEndedAt, string TriggerConfigVersion, string TriggerReason,
    IReadOnlyList<string> SegmentIds, string MediaFileName, long MediaSizeBytes, string MediaSha256,
    string ThumbnailFileName, long ThumbnailSizeBytes, string ThumbnailSha256, DateTimeOffset ArchivedAt);

public sealed record ClipArchiveReceiptV1(string ManifestFileName, string ManifestSha256,
    string MediaFileName, string MediaSha256, string ThumbnailFileName, string ThumbnailSha256,
    DateTimeOffset ArchivedAt);

public interface IClipArchiveAdapter
{
    Task<ClipArchiveReceiptV1> ArchiveAsync(ClipManifestV1 clip, string mediaPath, string thumbnailPath,
        CancellationToken cancellationToken);
}

public sealed class LocalFilesystemClipArchiveAdapter(ClipArchiveOptions options) : IClipArchiveAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ClipArchiveReceiptV1> ArchiveAsync(ClipManifestV1 clip, string mediaPath,
        string thumbnailPath, CancellationToken cancellationToken)
    {
        if (!options.Enabled || clip.State != "ready" || !LocalSegmentSpool.IsSafeSegmentId(clip.ClipId)
            || !PublicIdFormat.IsValid(clip.PublicId, PublicIdPrefixes.ClipPrefix)
            || !IsSafeCameraId(clip.CameraId) || clip.SegmentIds is not { Count: > 0 }
            || clip.SegmentIds.Any(segmentId => !LocalSegmentSpool.IsSafeSegmentId(segmentId))
            || string.IsNullOrWhiteSpace(clip.TriggerConfigVersion) || clip.TriggerConfigVersion.Length > 64
            || string.IsNullOrWhiteSpace(clip.TriggerReason) || clip.TriggerReason.Length > 256
            || clip.RequestedEndedAt < clip.RequestedStartedAt || clip.EffectiveEndedAt < clip.EffectiveStartedAt
            || clip.MediaFileName != clip.ClipId + ".ts"
            || clip.ThumbnailFileName != clip.ClipId + ".jpg" || !IsSha256(clip.MediaSha256)
            || !IsSha256(clip.ThumbnailSha256)) throw new InvalidDataException("clip_archive_source_invalid");

        ValidateSourcePath(mediaPath, clip.MediaFileName);
        ValidateSourcePath(thumbnailPath, clip.ThumbnailFileName);
        var mediaInfo = new FileInfo(mediaPath);
        var thumbnailInfo = new FileInfo(thumbnailPath);
        if (!mediaInfo.Exists || !thumbnailInfo.Exists || mediaInfo.Length is < 1 || thumbnailInfo.Length is < 1
            || mediaInfo.Length > options.MaximumClipBytes || mediaInfo.Length + thumbnailInfo.Length > options.MaximumClipBytes)
            throw new InvalidDataException("clip_archive_size_invalid");

        var mediaHash = await HashAsync(mediaPath, cancellationToken);
        var thumbnailHash = await HashAsync(thumbnailPath, cancellationToken);
        if (mediaHash != clip.MediaSha256 || thumbnailHash != clip.ThumbnailSha256)
            throw new InvalidDataException("clip_archive_source_checksum_mismatch");

        var destinationRoot = options.DestinationDirectory!;
        EnsureNoReparsePointInPath(destinationRoot);
        Directory.CreateDirectory(destinationRoot);
        EnsureNotReparsePoint(destinationRoot);
        var cameraDirectory = Path.Combine(destinationRoot, clip.CameraId);
        var clipDirectory = Path.Combine(cameraDirectory, clip.ClipId);
        Directory.CreateDirectory(cameraDirectory);
        EnsureNotReparsePoint(cameraDirectory);
        Directory.CreateDirectory(clipDirectory);
        EnsureNotReparsePoint(clipDirectory);
        var archivedMedia = Path.Combine(clipDirectory, clip.MediaFileName);
        var archivedThumbnail = Path.Combine(clipDirectory, clip.ThumbnailFileName);
        var archivedManifest = Path.Combine(clipDirectory, "archive-manifest.json");

        if (File.Exists(archivedManifest))
        {
            var existing = await ReadAndValidateManifestAsync(archivedManifest, clip, cancellationToken);
            if (!File.Exists(archivedMedia) || !File.Exists(archivedThumbnail))
                throw new InvalidDataException("clip_archive_commit_files_missing");
            if (await HashAsync(archivedMedia, cancellationToken) != existing.MediaSha256
                || await HashAsync(archivedThumbnail, cancellationToken) != existing.ThumbnailSha256)
                throw new InvalidDataException("clip_archive_commit_integrity_failure");
            var existingManifestHash = await HashAsync(archivedManifest, cancellationToken);
            return new ClipArchiveReceiptV1("archive-manifest.json", existingManifestHash, clip.MediaFileName,
                clip.MediaSha256!, clip.ThumbnailFileName, clip.ThumbnailSha256!, existing.ArchivedAt);
        }

        var usage = MeasureUsage(destinationRoot);
        var additionalBytes = mediaInfo.Length + thumbnailInfo.Length;
        if (usage.ClipCount >= options.MaximumClips || usage.TotalBytes + additionalBytes > options.MaximumBytes)
            throw new IOException("clip_archive_capacity_reached");

        await CopyVerifiedAsync(mediaPath, archivedMedia, clip.MediaSha256!, cancellationToken);
        await CopyVerifiedAsync(thumbnailPath, archivedThumbnail, clip.ThumbnailSha256!, cancellationToken);

        ArchivedClipManifestV1 archiveManifest;
        archiveManifest = new ArchivedClipManifestV1("1.0", clip.ClipId, clip.PublicId ?? string.Empty, clip.CameraId,
            clip.RequestedStartedAt, clip.RequestedEndedAt, clip.EffectiveStartedAt, clip.EffectiveEndedAt,
            clip.TriggerConfigVersion, clip.TriggerReason, clip.SegmentIds, clip.MediaFileName, mediaInfo.Length,
            clip.MediaSha256!, clip.ThumbnailFileName, thumbnailInfo.Length, clip.ThumbnailSha256!, DateTimeOffset.UtcNow);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(archiveManifest, JsonOptions);
        await WriteNewOrVerifyAsync(archivedManifest, manifestBytes, cancellationToken);

        var manifestHash = await HashAsync(archivedManifest, cancellationToken);
        return new ClipArchiveReceiptV1("archive-manifest.json", manifestHash, clip.MediaFileName,
            clip.MediaSha256!, clip.ThumbnailFileName, clip.ThumbnailSha256!, archiveManifest.ArchivedAt);
    }

    private async Task CopyVerifiedAsync(string sourcePath, string destinationPath, string expectedHash,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath))
        {
            if (await HashAsync(destinationPath, cancellationToken) != expectedHash)
                throw new InvalidDataException("clip_archive_existing_checksum_mismatch");
            return;
        }

        var temporary = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             81_920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81_920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(target, cancellationToken);
                await target.FlushAsync(cancellationToken);
                target.Flush(flushToDisk: true);
            }
            if (await HashAsync(temporary, cancellationToken) != expectedHash)
                throw new InvalidDataException("clip_archive_copy_checksum_mismatch");
            try { File.Move(temporary, destinationPath, overwrite: false); }
            catch (IOException) when (File.Exists(destinationPath))
            {
                if (await HashAsync(destinationPath, cancellationToken) != expectedHash)
                    throw new InvalidDataException("clip_archive_existing_checksum_mismatch");
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<ArchivedClipManifestV1> ReadAndValidateManifestAsync(string path, ClipManifestV1 clip,
        CancellationToken cancellationToken)
    {
        ArchivedClipManifestV1? manifest;
        try
        {
            await using var stream = File.OpenRead(path);
            manifest = await JsonSerializer.DeserializeAsync<ArchivedClipManifestV1>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException) { throw new InvalidDataException("clip_archive_manifest_invalid"); }
        if (manifest is null || manifest.SchemaVersion != "1.0" || manifest.ClipId != clip.ClipId
            || manifest.PublicId != clip.PublicId
            || manifest.CameraId != clip.CameraId || manifest.MediaSha256 != clip.MediaSha256
            || manifest.ThumbnailSha256 != clip.ThumbnailSha256 || manifest.MediaFileName != clip.MediaFileName
            || manifest.ThumbnailFileName != clip.ThumbnailFileName
            || manifest.RequestedStartedAt != clip.RequestedStartedAt || manifest.RequestedEndedAt != clip.RequestedEndedAt
            || manifest.EffectiveStartedAt != clip.EffectiveStartedAt || manifest.EffectiveEndedAt != clip.EffectiveEndedAt
            || manifest.TriggerConfigVersion != clip.TriggerConfigVersion || manifest.TriggerReason != clip.TriggerReason
            || !manifest.SegmentIds.SequenceEqual(clip.SegmentIds, StringComparer.Ordinal)
            || manifest.MediaSizeBytes != new FileInfo(Path.Combine(Path.GetDirectoryName(path)!, clip.MediaFileName!)).Length
            || manifest.ThumbnailSizeBytes != new FileInfo(Path.Combine(Path.GetDirectoryName(path)!, clip.ThumbnailFileName!)).Length
            || !IsSha256(manifest.MediaSha256) || !IsSha256(manifest.ThumbnailSha256))
            throw new InvalidDataException("clip_archive_manifest_conflict");
        return manifest;
    }

    private async Task WriteNewOrVerifyAsync(string destination, byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16_384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination))
            {
                var existing = await File.ReadAllBytesAsync(destination, cancellationToken);
                if (!existing.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("clip_archive_manifest_conflict");
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateSourcePath(string path, string expectedFileName)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(path) != expectedFileName || !File.Exists(path))
            throw new InvalidDataException("clip_archive_source_path_invalid");
        EnsureNoReparsePointInPath(path);
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("clip_archive_destination_reparse_point");
    }

    private static void EnsureNoReparsePointInPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = Path.GetPathRoot(fullPath)!;
        foreach (var part in fullPath[current.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (Directory.Exists(current) || File.Exists(current)) EnsureNotReparsePoint(current);
            else break;
        }
    }

    private static bool IsSafeCameraId(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static (int ClipCount, long TotalBytes) MeasureUsage(string root)
    {
        var clipCount = 0;
        long totalBytes = 0;
        foreach (var cameraDirectory in Directory.EnumerateDirectories(root))
        {
            EnsureNotReparsePoint(cameraDirectory);
            foreach (var clipDirectory in Directory.EnumerateDirectories(cameraDirectory))
            {
                EnsureNotReparsePoint(clipDirectory);
                if (File.Exists(Path.Combine(clipDirectory, "archive-manifest.json"))) clipCount++;
                foreach (var file in Directory.EnumerateFiles(clipDirectory))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("clip_archive_destination_reparse_point");
                    totalBytes = checked(totalBytes + new FileInfo(file).Length);
                }
            }
        }
        return (clipCount, totalBytes);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}
