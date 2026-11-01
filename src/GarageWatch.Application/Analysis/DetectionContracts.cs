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

/// <summary>Versioned, model-neutral boundary for a face crop already selected by an authorized local workflow.</summary>
public sealed record FaceCropV1(string MediaType, int Width, int Height, ReadOnlyMemory<byte> EncodedImage)
{
    public const string SchemaVersion = "face-crop.v1";
    public const int MaximumEncodedBytes = 2 * 1024 * 1024;

    public bool IsValid => (MediaType is "image/jpeg" or "image/png") &&
        Width is > 0 and <= 2048 && Height is > 0 and <= 2048 &&
        !EncodedImage.IsEmpty && EncodedImage.Length <= MaximumEncodedBytes;
}

/// <summary>Transient embedding result. Callers must not persist it until biometric storage policy is approved.</summary>
public sealed record FaceEmbeddingV1(string SchemaVersion, string ModelId, string ModelVersion,
    int Dimensions, IReadOnlyList<float> Values)
{
    public const string CurrentSchemaVersion = "face-embedding.v1";

    public bool IsValid => SchemaVersion == CurrentSchemaVersion && !string.IsNullOrWhiteSpace(ModelId) &&
        !string.IsNullOrWhiteSpace(ModelVersion) && Dimensions is > 0 and <= 4096 &&
        Values is not null && Values.Count == Dimensions &&
        Values.All(float.IsFinite);
}

public interface IFaceEmbeddingExtractor
{
    string ModelId { get; }
    string ModelVersion { get; }
    ValueTask<FaceEmbeddingV1> ExtractAsync(FaceCropV1 crop, CancellationToken cancellationToken);
}

public sealed record TrackedObject(string TrackId, string ClassName, int ClassId, float Score, DetectionBox Box);
