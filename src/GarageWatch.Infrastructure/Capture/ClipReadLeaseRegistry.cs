using System.Collections.Concurrent;

namespace GarageWatch.Infrastructure.Capture;

/// <summary>Coordinates clip reads/preservation changes with retention deletion in this API process.</summary>
public sealed class ClipReadLeaseRegistry
{
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public IDisposable? TryAcquireRead(string publicClipId) => TryAcquire(publicClipId, purging: false);

    public IDisposable? TryAcquireMutation(string publicClipId) => TryAcquire(publicClipId, purging: false);

    public IDisposable? TryBeginPurge(string publicClipId) => TryAcquire(publicClipId, purging: true);

    private IDisposable? TryAcquire(string publicClipId, bool purging)
    {
        var entry = entries.GetOrAdd(publicClipId, static _ => new Entry());
        lock (entry.Sync)
        {
            if (entry.Purging || (purging && entry.Readers != 0)) return null;
            if (purging) entry.Purging = true;
            else entry.Readers++;
            return new Lease(entry, purging);
        }
    }

    private sealed class Entry
    {
        public object Sync { get; } = new();
        public int Readers { get; set; }
        public bool Purging { get; set; }
    }

    private sealed class Lease(Entry entry, bool purging) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lock (entry.Sync)
            {
                if (purging) entry.Purging = false;
                else entry.Readers--;
            }
        }
    }
}
