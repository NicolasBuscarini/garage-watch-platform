namespace GarageWatch.Infrastructure.Capture;

public sealed record CapturedSegment(string SourcePath, DateTimeOffset StartedAt, DateTimeOffset EndedAt);

public interface ISegmentCaptureAdapter
{
    string Name { get; }
    string Version { get; }
    IReadOnlyList<string> Capabilities { get; }
    IAsyncEnumerable<CapturedSegment> CaptureAsync(string workDirectory, CancellationToken cancellationToken);
}

public sealed record CaptureStatus(string Adapter, string AdapterVersion, IReadOnlyList<string> Capabilities,
    string State, string? StreamEpoch, long Gaps, long Segments, string? LastErrorCode);
