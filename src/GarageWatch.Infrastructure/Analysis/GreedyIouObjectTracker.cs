using GarageWatch.Application.Analysis;

namespace GarageWatch.Infrastructure.Analysis;

public sealed class GreedyIouObjectTracker(float minimumIou = 0.3f, int maximumMissedFrames = 10) : IObjectTracker
{
    private sealed record Track(string Id, string ClassName, int ClassId, DetectionBox Box, int MissedFrames);
    private readonly Dictionary<(string Camera, string Epoch), List<Track>> _tracks = [];
    private readonly Dictionary<(string Camera, string Epoch), long> _nextIds = [];

    public IReadOnlyList<TrackedObject> Update(DetectionFrame frame, IReadOnlyList<ObjectDetection> detections)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(detections);
        var key = (frame.CameraId, frame.StreamEpoch);
        foreach (var staleKey in _tracks.Keys.Where(candidate => candidate.Camera == frame.CameraId
                     && candidate.Epoch != frame.StreamEpoch).ToArray())
        {
            _tracks.Remove(staleKey);
            _nextIds.Remove(staleKey);
        }
        if (!_tracks.TryGetValue(key, out var tracks)) _tracks[key] = tracks = [];
        var candidates = from trackIndex in Enumerable.Range(0, tracks.Count)
                         from detectionIndex in Enumerable.Range(0, detections.Count)
                         where tracks[trackIndex].ClassId == detections[detectionIndex].ClassId
                         let iou = IntersectionOverUnion(tracks[trackIndex].Box, detections[detectionIndex].Box)
                         where iou >= minimumIou
                         orderby iou descending
                         select (trackIndex, detectionIndex, iou);
        var matchedTracks = new HashSet<int>();
        var matchedDetections = new HashSet<int>();
        var resultIds = new string?[detections.Count];
        foreach (var (trackIndex, detectionIndex, _) in candidates)
        {
            if (matchedTracks.Contains(trackIndex) || matchedDetections.Contains(detectionIndex)) continue;
            matchedTracks.Add(trackIndex);
            matchedDetections.Add(detectionIndex);
            resultIds[detectionIndex] = tracks[trackIndex].Id;
        }

        var updated = new List<Track>(Math.Min(detections.Count, 256));
        for (var index = 0; index < tracks.Count; index++)
        {
            if (matchedTracks.Contains(index)) continue;
            var missed = tracks[index] with { MissedFrames = tracks[index].MissedFrames + 1 };
            if (missed.MissedFrames <= maximumMissedFrames) updated.Add(missed);
        }
        for (var index = 0; index < detections.Count; index++)
        {
            var detection = detections[index];
            var id = resultIds[index];
            if (id is null)
            {
                var next = _nextIds.GetValueOrDefault(key) + 1;
                _nextIds[key] = next;
                id = $"{frame.CameraId}:{frame.StreamEpoch}:{next}";
            }
            resultIds[index] = id;
            updated.Add(new Track(id, detection.ClassName, detection.ClassId, detection.Box, 0));
        }
        _tracks[key] = updated;
        return detections.Select((detection, index) => new TrackedObject(resultIds[index]!, detection.ClassName,
            detection.ClassId, detection.Score, detection.Box)).ToArray();
    }

    public void Reset(string cameraId, string streamEpoch)
    {
        _tracks.Remove((cameraId, streamEpoch));
        _nextIds.Remove((cameraId, streamEpoch));
    }

    private static float IntersectionOverUnion(DetectionBox left, DetectionBox right)
    {
        var width = Math.Max(0, Math.Min(left.X2, right.X2) - Math.Max(left.X1, right.X1));
        var height = Math.Max(0, Math.Min(left.Y2, right.Y2) - Math.Max(left.Y1, right.Y1));
        var intersection = width * height;
        var leftArea = (left.X2 - left.X1) * (left.Y2 - left.Y1);
        var rightArea = (right.X2 - right.X1) * (right.Y2 - right.Y1);
        return intersection / (leftArea + rightArea - intersection);
    }
}
