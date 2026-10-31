using System.Security.Cryptography;
using System.Text.Json;

namespace GarageWatch.Infrastructure.Capture;

public sealed record ClipManifestV1(
    string SchemaVersion,
    string ClipId,
    string CameraId,
    DateTimeOffset RequestedStartedAt,
    DateTimeOffset RequestedEndedAt,
    DateTimeOffset EffectiveStartedAt,
    DateTimeOffset EffectiveEndedAt,
    string State,
    string TriggerConfigVersion,
    string TriggerReason,
    IReadOnlyList<string> SegmentIds,
    string? MediaFileName,
    string? MediaSha256,
    string? ThumbnailFileName,
    string? ThumbnailSha256,
    int AttemptCount,
    DateTimeOffset UpdatedAt,
    string ArchiveStatus = "not_requested",
    int ArchiveAttemptCount = 0,
    DateTimeOffset? ArchiveNextAttemptAt = null,
    DateTimeOffset? ArchivedAt = null,
    string? ArchiveManifestSha256 = null,
    string? PublicId = null);

public interface IClipMediaAssembler
{
    Task<(string MediaFileName, string MediaSha256, string ThumbnailFileName, string ThumbnailSha256)> AssembleAsync(
        ClipManifestV1 clip, IReadOnlyList<string> segmentPaths, CancellationToken cancellationToken);
}

public sealed class LocalClipMediaAssembler(CaptureOptions options) : IClipMediaAssembler
{
    public async Task<(string MediaFileName, string MediaSha256, string ThumbnailFileName, string ThumbnailSha256)> AssembleAsync(
        ClipManifestV1 clip, IReadOnlyList<string> segmentPaths, CancellationToken cancellationToken)
    {
        var mediaDirectory = Path.Combine(options.SpoolDirectory, "clips", "media");
        var thumbnailDirectory = Path.Combine(options.SpoolDirectory, "clips", "thumbnails");
        Directory.CreateDirectory(mediaDirectory);
        Directory.CreateDirectory(thumbnailDirectory);
        var mediaName = $"{clip.ClipId}.ts";
        var mediaPath = Path.Combine(mediaDirectory, mediaName);
        var mediaTemporary = mediaPath + ".tmp";
        var thumbnailName = $"{clip.ClipId}.jpg";
        var thumbnailPath = Path.Combine(thumbnailDirectory, thumbnailName);
        var thumbnailTemporary = thumbnailPath + ".tmp";
        try
        {
            await using (var target = new FileStream(mediaTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                foreach (var segment in segmentPaths)
                {
                    await using var source = File.OpenRead(segment);
                    await source.CopyToAsync(target, cancellationToken);
                }
            }
            var mediaHash = await HashAsync(mediaTemporary, cancellationToken);
            await CreateThumbnailAsync(mediaTemporary, thumbnailTemporary, cancellationToken);
            var thumbnailHash = await HashAsync(thumbnailTemporary, cancellationToken);
            File.Move(mediaTemporary, mediaPath);
            File.Move(thumbnailTemporary, thumbnailPath);
            return (mediaName, mediaHash, thumbnailName, thumbnailHash);
        }
        finally
        {
            if (File.Exists(mediaTemporary)) File.Delete(mediaTemporary);
            if (File.Exists(thumbnailTemporary)) File.Delete(thumbnailTemporary);
        }
    }

    private async Task CreateThumbnailAsync(string mediaPath, string destination, CancellationToken cancellationToken)
    {
        var start = new System.Diagnostics.ProcessStartInfo(options.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", mediaPath,
                     "-frames:v", "1", "-q:v", "4", "-f", "image2", destination }) start.ArgumentList.Add(argument);
        using var process = new System.Diagnostics.Process { StartInfo = start };
        if (!process.Start()) throw new InvalidDataException("clip_thumbnail_failed");
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(stderr, stdout);
        if (process.ExitCode != 0 || !File.Exists(destination) || new FileInfo(destination).Length == 0)
            throw new InvalidDataException("clip_thumbnail_failed");
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}

public interface IVisualChangeDetector
{
    Task<bool> HasRelevantChangeAsync(string segmentPath, CancellationToken cancellationToken);
}

public sealed class FfmpegVisualChangeDetector(CaptureOptions capture, ClipTriggerOptions options) : IVisualChangeDetector
{
    private byte[]? previous;

    public async Task<bool> HasRelevantChangeAsync(string segmentPath, CancellationToken cancellationToken)
    {
        var start = new System.Diagnostics.ProcessStartInfo(capture.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", segmentPath,
                     "-vf", $"fps={options.SampleFps},scale={options.Width}:{options.Height}", "-frames:v", "1",
                     "-f", "rawvideo", "-pix_fmt", "gray", "pipe:1" }) start.ArgumentList.Add(argument);
        using var process = new System.Diagnostics.Process { StartInfo = start };
        if (!process.Start()) return false;
        await using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(copy, errors);
        var frame = output.ToArray();
        if (process.ExitCode != 0 || frame.Length != options.Width * options.Height) return false;
        var old = Interlocked.Exchange(ref previous, frame);
        if (old is null || old.Length != frame.Length) return false;
        var changedEverywhere = 0;
        var changedInScope = 0;
        var scopedPixels = 0;
        for (var index = 0; index < frame.Length; index++)
        {
            var changed = Math.Abs(frame[index] - old[index]) >= options.PixelDelta;
            if (changed) changedEverywhere++;
            var x = (index % options.Width + .5) / options.Width;
            var y = (index / options.Width + .5) / options.Height;
            if (!(options.Zone ?? new NormalizedRectangle(0, 0, 1, 1)).Contains(x, y)
                || (options.Masks ?? []).Any(mask => mask.Contains(x, y))) continue;
            scopedPixels++;
            if (changed) changedInScope++;
        }
        if (changedEverywhere / (double)frame.Length >= options.GlobalChangedAreaRatio) return false;
        return scopedPixels > 0 && changedInScope / (double)scopedPixels >= options.ChangedAreaRatio;
    }
}
