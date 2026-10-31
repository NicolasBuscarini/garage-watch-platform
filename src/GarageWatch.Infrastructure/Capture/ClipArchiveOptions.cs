using Microsoft.Extensions.Configuration;

namespace GarageWatch.Infrastructure.Capture;

public sealed record ClipArchiveOptions(string? DestinationDirectory, int PollSeconds, int MaximumClips,
    long MaximumBytes, long MaximumClipBytes)
{
    public bool Enabled => DestinationDirectory is not null;

    public static ClipArchiveOptions FromConfiguration(IConfiguration configuration, CaptureOptions capture)
    {
        var destination = configuration["CLIP_ARCHIVE_DIRECTORY"]?.Trim();
        if (string.IsNullOrEmpty(destination)) destination = null;
        if (destination is not null)
        {
            if (!Path.IsPathFullyQualified(destination))
                throw new InvalidOperationException("CLIP_ARCHIVE_DIRECTORY deve ser absoluto.");
            var spool = Path.GetFullPath(capture.SpoolDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var archive = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (archive.StartsWith(spool, StringComparison.Ordinal) || spool.StartsWith(archive, StringComparison.Ordinal))
                throw new InvalidOperationException("CLIP_ARCHIVE_DIRECTORY deve ser separado do spool de captura.");
        }

        return new ClipArchiveOptions(destination,
            Parse(configuration["CLIP_ARCHIVE_POLL_SECONDS"], 2, 1, 60),
            Parse(configuration["CLIP_ARCHIVE_MAX_CLIPS"], 10_000, 1, 100_000),
            ParseLong(configuration["CLIP_ARCHIVE_MAX_BYTES"], 53_687_091_200, 1_048_576, 1_099_511_627_776),
            ParseLong(configuration["CLIP_ARCHIVE_MAX_CLIP_BYTES"], 1_073_741_824, 1_048_576, 10_737_418_240));
    }

    private static int Parse(string? raw, int fallback, int minimum, int maximum) => raw is null ? fallback
        : int.TryParse(raw, out var value) && value >= minimum && value <= maximum
            ? value : throw new InvalidOperationException("Configuração de arquivamento fora do limite permitido.");

    private static long ParseLong(string? raw, long fallback, long minimum, long maximum) => raw is null ? fallback
        : long.TryParse(raw, out var value) && value >= minimum && value <= maximum
            ? value : throw new InvalidOperationException("Configuração de arquivamento fora do limite permitido.");
}
