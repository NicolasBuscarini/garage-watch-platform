using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace GarageWatch.Infrastructure.Capture;

/// <summary>
/// Bounded cache of browser-playable copies. The authoritative clip stays an MPEG-TS concatenation
/// (contract and archive unchanged); this cache only holds a remuxed, video-only MP4 per played clip.
/// </summary>
public sealed record ClipPlaybackOptions(string Directory, long MaximumBytes, int TimeoutSeconds)
{
    public const long DefaultMaximumBytes = 2L * 1024 * 1024 * 1024;
    public const int DefaultTimeoutSeconds = 30;

    public static ClipPlaybackOptions FromEnvironment(IConfiguration configuration, CaptureOptions capture)
    {
        var directory = Path.Combine(capture.SpoolDirectory, "clips", "playback");
        var maximumBytes = DefaultMaximumBytes;
        var raw = configuration["CLIP_PLAYBACK_CACHE_BYTES"];
        if (!string.IsNullOrWhiteSpace(raw) &&
            (!long.TryParse(raw, out maximumBytes) || maximumBytes is < 67_108_864 or > 68_719_476_736))
            throw new InvalidOperationException("CLIP_PLAYBACK_CACHE_BYTES deve estar entre 64 MiB e 64 GiB.");
        return new ClipPlaybackOptions(directory, maximumBytes, DefaultTimeoutSeconds);
    }
}

/// <summary>
/// Remuxes a stored clip into a fragmented-free MP4 (H.264 copy, faststart, no audio) so browsers can
/// play it. Derived files are keyed by the source hash, written atomically and pruned by total size.
/// </summary>
public sealed class ClipPlaybackCache(ClipPlaybackOptions options, CaptureOptions capture,
    ILogger<ClipPlaybackCache> logger)
{
    private readonly SemaphoreSlim conversionGate = new(1, 1);

    public string? ResolveExisting(string clipId, string mediaSha256)
    {
        var path = PathFor(clipId, mediaSha256);
        return path is not null && File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    public async Task<string?> EnsureAsync(string clipId, string sourcePath, string mediaSha256,
        CancellationToken cancellationToken)
    {
        if (PathFor(clipId, mediaSha256) is not { } destination) return null;
        var existing = ResolveExisting(clipId, mediaSha256);
        if (existing is not null) return existing;
        await conversionGate.WaitAsync(cancellationToken);
        try
        {
            existing = ResolveExisting(clipId, mediaSha256);
            if (existing is not null) return existing;
            Directory.CreateDirectory(options.Directory);
            var temporary = Path.Combine(options.Directory,
                $"{Path.GetFileNameWithoutExtension(destination)}-{Guid.NewGuid():N}.tmp.mp4");
            try
            {
                if (!await RemuxAsync(clipId, sourcePath, temporary, cancellationToken)) return null;
                if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                {
                    logger.LogWarning("Cópia de reprodução do clipe vazia; mídia original preservada. clip={ClipId}",
                        clipId);
                    return null;
                }
                File.Move(temporary, destination, overwrite: true);
                Prune(destination);
                return destination;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            conversionGate.Release();
        }
    }

    private string? PathFor(string clipId, string mediaSha256)
    {
        if (!LocalSegmentSpool.IsSafeSegmentId(clipId)) return null;
        if (mediaSha256 is not { Length: 64 } || !mediaSha256.All(Uri.IsHexDigit)) return null;
        return Path.Combine(options.Directory, $"{clipId}-{mediaSha256[..12]}.mp4");
    }

    private async Task<bool> RemuxAsync(string clipId, string sourcePath, string destination,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(capture.FfmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", sourcePath,
                     "-map", "0:v:0", "-an", "-c", "copy", "-movflags", "+faststart", "-f", "mp4", destination
                 })
            start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null) return false;
            var stderr = await process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode == 0) return true;
            logger.LogWarning("Remux de reprodução do clipe falhou; mídia original preservada. clip={ClipId} exit={ExitCode}",
                clipId, process.ExitCode);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            logger.LogWarning("Remux de reprodução do clipe não concluiu no tempo limite. clip={ClipId}", clipId);
            return false;
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            process?.Dispose();
        }
    }

    private void Prune(string keepPath)
    {
        var files = new DirectoryInfo(options.Directory).EnumerateFiles("*.mp4")
            .Where(file => file.FullName != Path.GetFullPath(keepPath))
            .OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        var total = files.Sum(file => file.Length) + new FileInfo(keepPath).Length;
        foreach (var file in files)
        {
            if (total <= options.MaximumBytes) break;
            try
            {
                var length = file.Length;
                file.Delete();
                total -= length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
