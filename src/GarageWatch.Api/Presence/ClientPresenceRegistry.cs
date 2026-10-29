namespace GarageWatch.Api.Presence;

public sealed class ClientPresenceRegistry(TimeProvider timeProvider)
{
    public const int MaximumClients = 64;
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _lastHeartbeat = [];

    public bool Heartbeat(Guid clientId, out int activeClients)
    {
        if (clientId == Guid.Empty)
        {
            activeClients = 0;
            return false;
        }

        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            Expire(now);
            if (!_lastHeartbeat.ContainsKey(clientId) && _lastHeartbeat.Count >= MaximumClients)
            {
                activeClients = _lastHeartbeat.Count;
                return false;
            }

            _lastHeartbeat[clientId] = now;
            activeClients = _lastHeartbeat.Count;
            return true;
        }
    }

    private void Expire(DateTimeOffset now)
    {
        var deadline = now - LeaseDuration;
        foreach (var clientId in _lastHeartbeat.Where(entry => entry.Value <= deadline)
                     .Select(entry => entry.Key).ToArray())
            _lastHeartbeat.Remove(clientId);
    }
}
