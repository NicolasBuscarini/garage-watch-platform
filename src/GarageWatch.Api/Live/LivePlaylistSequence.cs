namespace GarageWatch.Api.Live;

public sealed class LivePlaylistSequence
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _sequenceBySegment = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private long _nextSequence;

    public static bool StartsDiscontinuity(IReadOnlyList<string> orderedStreamEpochs, int index) =>
        index > 0 && index < orderedStreamEpochs.Count && orderedStreamEpochs[index] != orderedStreamEpochs[index - 1];

    public long GetStartSequence(IReadOnlyList<string> orderedSegmentIds, int maximumSegments)
    {
        if (orderedSegmentIds.Count == 0) return _nextSequence;

        lock (_gate)
        {
            var currentIds = new HashSet<string>(orderedSegmentIds, StringComparer.Ordinal);
            foreach (var id in orderedSegmentIds)
            {
                if (_sequenceBySegment.ContainsKey(id)) continue;
                _sequenceBySegment.Add(id, _nextSequence++);
                _insertionOrder.Enqueue(id);
            }

            var historyLimit = Math.Max(32, maximumSegments * 2);
            var candidatesToCheck = _insertionOrder.Count;
            while (_sequenceBySegment.Count > historyLimit && candidatesToCheck-- > 0)
            {
                var oldestId = _insertionOrder.Dequeue();
                if (currentIds.Contains(oldestId))
                {
                    _insertionOrder.Enqueue(oldestId);
                    continue;
                }
                _sequenceBySegment.Remove(oldestId);
            }

            return _sequenceBySegment[orderedSegmentIds[0]];
        }
    }
}
