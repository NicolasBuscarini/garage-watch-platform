namespace GarageWatch.Application.Analysis;

public sealed class DetectionProcessor(IObjectDetector detector, IObjectTracker tracker)
{
    public async ValueTask<DetectionResultV1> ProcessAsync(DetectionFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (string.IsNullOrWhiteSpace(frame.CameraId) || string.IsNullOrWhiteSpace(frame.StreamEpoch)
            || string.IsNullOrWhiteSpace(frame.FrameId) || frame.Width <= 0 || frame.Height <= 0)
            throw new ArgumentException("Invalid detection frame.", nameof(frame));
        if (frame.RgbNchw.Length != checked(frame.Width * frame.Height * 3))
            throw new ArgumentException("Frame tensor size does not match dimensions.", nameof(frame));

        var detections = await detector.DetectAsync(frame, cancellationToken);
        var valid = detections.Where(detection => detection.Box.IsValid && float.IsFinite(detection.Score)
            && detection.Score is >= 0 and <= 1 && detection.ClassId >= 0
            && !string.IsNullOrWhiteSpace(detection.ClassName) && detection.ClassName.Length <= 64).Take(256).ToArray();
        var tracked = tracker.Update(frame, valid);
        return new DetectionResultV1("1.0", frame.CameraId, frame.StreamEpoch, frame.FrameId, frame.CapturedAt,
            DateTimeOffset.UtcNow, frame.Width, frame.Height, detector.ModelVersion, tracked);
    }
}
