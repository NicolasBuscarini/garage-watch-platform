namespace GarageWatch.Application.Analysis;

public sealed record DetectionFrame(
    string CameraId,
    string StreamEpoch,
    string FrameId,
    DateTimeOffset CapturedAt,
    int Width,
    int Height,
    ReadOnlyMemory<float> RgbNchw);

public sealed record DetectionBox(float X1, float Y1, float X2, float Y2)
{
    public bool IsValid => float.IsFinite(X1) && float.IsFinite(Y1) && float.IsFinite(X2) && float.IsFinite(Y2)
        && X1 >= 0 && Y1 >= 0 && X2 <= 1 && Y2 <= 1 && X2 > X1 && Y2 > Y1;
}

public sealed record ObjectDetection(string ClassName, int ClassId, float Score, DetectionBox Box);

public sealed record DetectionResultV1(
    string SchemaVersion,
    string CameraId,
    string StreamEpoch,
    string FrameId,
    DateTimeOffset CapturedAt,
    DateTimeOffset ProcessedAt,
    int FrameWidth,
    int FrameHeight,
    string ModelVersion,
    IReadOnlyList<TrackedObject> Detections);

public interface IObjectDetector
{
    string ModelVersion { get; }
    ValueTask<IReadOnlyList<ObjectDetection>> DetectAsync(DetectionFrame frame, CancellationToken cancellationToken);
}

public interface IObjectTracker
{
    IReadOnlyList<TrackedObject> Update(DetectionFrame frame, IReadOnlyList<ObjectDetection> detections);
    void Reset(string cameraId, string streamEpoch);
}

public sealed record TrackedObject(string TrackId, string ClassName, int ClassId, float Score, DetectionBox Box);
