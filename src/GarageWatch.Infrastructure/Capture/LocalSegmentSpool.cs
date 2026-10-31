using System.Security.Cryptography;
using System.Text.Json;

namespace GarageWatch.Infrastructure.Capture;

public sealed record SegmentManifestV1(
    string SchemaVersion,
    string SegmentId,
    string CameraId,
    string StreamEpoch,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string FileName,
    long SizeBytes,
    string Sha256);
public sealed record CaptureGapV1(string SchemaVersion, string GapId, string CameraId, string? PreviousStreamEpoch,
    DateTimeOffset OccurredAt, string ReasonCode, int RetryAttempt);

public sealed class LocalSegmentSpool(CaptureOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _manifestDirectory = Path.Combine(options.SpoolDirectory, "manifests");
    private readonly string _segmentDirectory = Path.Combine(options.SpoolDirectory, "segments");

    public IReadOnlyList<(SegmentManifestV1 Manifest, string Path)> GetRecentSegments(int maximum)
    {
        if (!Directory.Exists(_manifestDirectory)) return [];
        var recent = new List<(SegmentManifestV1 Manifest, string Path)>(maximum);
        foreach (var manifestPath in Directory.EnumerateFiles(_manifestDirectory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(manifestPath);
            if (!IsSafeSegmentId(id)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<SegmentManifestV1>(File.ReadAllText(manifestPath), JsonOptions);
                if (!IsValidManifest(manifest, id, options.CameraId, options.MaximumSegmentBytes) || manifest is null) continue;
                var segmentPath = Path.Combine(_segmentDirectory, manifest.FileName);
                var info = new FileInfo(segmentPath);
                if (!info.Exists || info.Length != manifest.SizeBytes) continue;
                recent.Add((manifest, segmentPath));
            }
            catch (Exception exception) when (exception is IOException or JsonException) { }
        }
        return recent.OrderBy(item => item.Manifest.StartedAt).TakeLast(maximum).ToArray();
    }

    public string? TryGetSegmentPath(SegmentManifestV1 manifest)
    {
        if (!IsValidManifest(manifest, manifest.SegmentId, options.CameraId, options.MaximumSegmentBytes)) return null;
        var path = Path.Combine(_segmentDirectory, manifest.FileName);
        return File.Exists(path) && new FileInfo(path).Length == manifest.SizeBytes ? path : null;
    }

    public IReadOnlySet<string> GetPinnedSegmentIds()
    {
        var directory = Path.Combine(options.SpoolDirectory, "clips", "manifests");
        if (!Directory.Exists(directory)) return new HashSet<string>(StringComparer.Ordinal);
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (!root.TryGetProperty("state", out var state) || state.GetString() != "collecting"
                    || !root.TryGetProperty("segmentIds", out var ids) || ids.ValueKind != JsonValueKind.Array) continue;
                foreach (var id in ids.EnumerateArray().Select(value => value.GetString()))
                    if (id is not null && IsSafeSegmentId(id)) pinned.Add(id);
            }
            catch (JsonException) { }
        }
        return pinned;
    }

    public async Task<SegmentManifestV1> StoreAsync(CapturedSegment segment, string epoch, CancellationToken cancellationToken)
    {
        if (segment.EndedAt < segment.StartedAt)
            throw new InvalidDataException("capture_segment_invalid");
        Directory.CreateDirectory(_manifestDirectory);
        Directory.CreateDirectory(_segmentDirectory);
        Directory.CreateDirectory(Path.Combine(options.SpoolDirectory, "work"));
        Directory.CreateDirectory(Path.Combine(options.SpoolDirectory, "gaps"));
        var id = Guid.NewGuid().ToString("N");
        var fileName = $"{id}.ts";
        var destination = Path.Combine(_segmentDirectory, fileName);
        var temporary = destination + ".tmp";
        var manifestPath = Path.Combine(_manifestDirectory, id + ".json");
        var manifestTemporary = manifestPath + ".tmp";
        try
        {
            await using (var source = File.OpenRead(segment.SourcePath))
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    copied += read;
                    if (copied > options.MaximumSegmentBytes) throw new InvalidDataException("capture_segment_size_limit");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            var sizeBytes = new FileInfo(temporary).Length;
            if (sizeBytes < 1) throw new InvalidDataException("capture_segment_invalid");
            string hash;
            await using (var file = File.OpenRead(temporary))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
            File.Move(temporary, destination);
            var manifest = new SegmentManifestV1("1.0", id, options.CameraId, epoch, segment.StartedAt, segment.EndedAt,
                fileName, sizeBytes, hash);
            await File.WriteAllTextAsync(manifestTemporary, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);
            File.Move(manifestTemporary, manifestPath);
            return manifest;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(manifestTemporary)) File.Delete(manifestTemporary);
            if (!File.Exists(manifestPath) && File.Exists(destination)) File.Delete(destination);
        }
    }

    public async Task<int> RecoverAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_manifestDirectory);
        Directory.CreateDirectory(_segmentDirectory);
        Directory.CreateDirectory(Path.Combine(options.SpoolDirectory, "work"));
        Directory.CreateDirectory(Path.Combine(options.SpoolDirectory, "gaps"));
        foreach (var temporary in Directory.EnumerateFiles(_segmentDirectory, "*.tmp")) File.Delete(temporary);
        foreach (var temporary in Directory.EnumerateFiles(_manifestDirectory, "*.tmp")) File.Delete(temporary);
        foreach (var temporary in Directory.EnumerateFiles(Path.Combine(options.SpoolDirectory, "gaps"), "*.tmp"))
            File.Delete(temporary);
        foreach (var work in Directory.EnumerateDirectories(Path.Combine(options.SpoolDirectory, "work")))
            Directory.Delete(work, recursive: true);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var valid = 0;
        foreach (var path in Directory.EnumerateFiles(_manifestDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestId = Path.GetFileNameWithoutExtension(path);
            var segmentPath = Path.Combine(_segmentDirectory, manifestId + ".ts");
            var isValid = false;
            try
            {
                var manifest = JsonSerializer.Deserialize<SegmentManifestV1>(await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
                if (!IsValidManifest(manifest, manifestId, options.CameraId, options.MaximumSegmentBytes))
                {
                    isValid = false;
                }
                else if (manifest is not null && File.Exists(segmentPath)
                    && new FileInfo(segmentPath).Length == manifest.SizeBytes)
                {
                    await using var file = File.OpenRead(segmentPath);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant();
                    isValid = hash == manifest.Sha256;
                }
            }
            catch (JsonException) { }
            if (isValid)
            {
                valid++;
                referenced.Add(manifestId);
                continue;
            }
            if (File.Exists(path)) File.Delete(path);
            if (manifestId.Length == 32 && manifestId.All(Uri.IsHexDigit) && File.Exists(segmentPath)) File.Delete(segmentPath);
        }
        foreach (var segment in Directory.EnumerateFiles(_segmentDirectory, "*.ts"))
            if (!referenced.Contains(Path.GetFileNameWithoutExtension(segment))) File.Delete(segment);

        var gapDirectory = Path.Combine(options.SpoolDirectory, "gaps");
        Directory.CreateDirectory(gapDirectory);
        return valid;
    }

    public async Task RecordGapAsync(string? previousEpoch, int retryAttempt, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(options.SpoolDirectory, "gaps");
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        var gap = new CaptureGapV1("1.0", id, options.CameraId, previousEpoch, DateTimeOffset.UtcNow,
            "capture_source_unavailable", retryAttempt);
        var temporary = Path.Combine(directory, "gap.pending.tmp");
        var path = Path.Combine(directory, id + ".json");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(gap, JsonOptions), cancellationToken);
            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        foreach (var old in Directory.GetFiles(directory, "*.json").OrderBy(File.GetCreationTimeUtc)
                     .Take(Math.Max(0, Directory.GetFiles(directory, "*.json").Length - options.MaximumSegments)))
        {
            try { File.Delete(old); }
            catch (IOException) { }
        }
    }

    public long CountGaps() => Directory.Exists(Path.Combine(options.SpoolDirectory, "gaps"))
        ? Directory.GetFiles(Path.Combine(options.SpoolDirectory, "gaps"), "*.json").LongLength : 0;

    public static bool IsValidManifest(SegmentManifestV1? manifest, string manifestId, string cameraId,
        long maximumSegmentBytes) => manifest is not null
        && manifest.SchemaVersion == "1.0"
        && IsSafeSegmentId(manifestId)
        && manifest.SegmentId == manifestId
        && IsSafeSegmentId(manifest.SegmentId)
        && Guid.TryParse(manifest.StreamEpoch, out _)
        && manifest.CameraId == cameraId
        && manifest.FileName == $"{manifestId}.ts"
        && manifest.FileName == Path.GetFileName(manifest.FileName)
        && manifest.SizeBytes is > 0 && manifest.SizeBytes <= maximumSegmentBytes
        && manifest.EndedAt >= manifest.StartedAt
        && manifest.Sha256 is { Length: 64 } hash && hash.All(Uri.IsHexDigit);

    public static bool IsSafeSegmentId(string? value) => value is { Length: 32 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
