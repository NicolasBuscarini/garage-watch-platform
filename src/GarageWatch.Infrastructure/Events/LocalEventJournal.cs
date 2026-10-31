using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using GarageWatch.Application.Events;

namespace GarageWatch.Infrastructure.Events;

public sealed record EventJournalEnvelopeV1(string SchemaVersion, string EventSha256, EventOccurrenceV1 Event);
public sealed record EventJournalRecoveryResult(int RecoveredEvents, int InvalidEvents, long JournalBytes);
public enum EventAppendStatus { Created, Duplicate, CapacityReached }

public sealed class LocalEventJournal(EventEngineOptions options)
{
    private static readonly Meter Meter = new("GarageWatch.Events", "1.0.0");
    private static readonly Counter<long> Appended = Meter.CreateCounter<long>("garagewatch.events.appended");
    private static readonly Counter<long> Duplicates = Meter.CreateCounter<long>("garagewatch.events.duplicates");
    private static readonly Counter<long> Dropped = Meter.CreateCounter<long>("garagewatch.events.dropped");
    private static readonly JsonSerializerOptions JsonOptions = EventEngineOptions.CreateJsonOptions();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _recovered;
    private int _eventCount;
    private long _journalBytes;

    public async Task<EventJournalRecoveryResult> RecoverAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(options.JournalDirectory);
            foreach (var temporary in Directory.EnumerateFiles(options.JournalDirectory, "*.tmp", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { File.Delete(temporary); }
                catch (IOException) { }
            }

            var valid = 0;
            var invalid = 0;
            long bytes = 0;
            foreach (var path in Directory.EnumerateFiles(options.JournalDirectory, "*.json", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var envelope = JsonSerializer.Deserialize<EventJournalEnvelopeV1>(await File.ReadAllBytesAsync(path, cancellationToken), JsonOptions);
                    if (!IsValid(envelope, path)) { invalid++; continue; }
                    valid++;
                    bytes += new FileInfo(path).Length;
                }
                catch (Exception exception) when (exception is IOException or JsonException or ArgumentException)
                {
                    invalid++;
                }
            }
            _eventCount = valid;
            _journalBytes = bytes;
            _recovered = true;
            return new EventJournalRecoveryResult(valid, invalid, bytes);
        }
        finally { _gate.Release(); }
    }

    public async Task<EventAppendStatus> AppendAsync(EventOccurrenceV1 occurrence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        if (!IsSafeIdentifier(occurrence.EventId) || !IsSafeIdentifier(occurrence.CameraId)
            || !IsSafeIdentifier(occurrence.StreamEpoch)) throw new ArgumentException("event_identity_invalid", nameof(occurrence));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_recovered) await RecoverUnderLockAsync(cancellationToken);
            var directory = Path.Combine(options.JournalDirectory, occurrence.CameraId, occurrence.StreamEpoch);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, occurrence.EventId + ".json");
            if (File.Exists(destination))
            {
                var existing = JsonSerializer.Deserialize<EventJournalEnvelopeV1>(
                    await File.ReadAllBytesAsync(destination, cancellationToken), JsonOptions);
                if (!IsValid(existing, destination) || existing!.Event.EventId != occurrence.EventId
                    || !string.Equals(existing.EventSha256,
                        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(occurrence, JsonOptions)))
                            .ToLowerInvariant(), StringComparison.Ordinal))
                    throw new InvalidDataException("event_id_conflict");
                Duplicates.Add(1, new KeyValuePair<string, object?>("outcome", "duplicate"));
                return EventAppendStatus.Duplicate;
            }

            var payload = JsonSerializer.SerializeToUtf8Bytes(occurrence, JsonOptions);
            var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            var envelope = new EventJournalEnvelopeV1("1.0", hash, occurrence);
            var serialized = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
            if (_eventCount >= options.MaximumEvents || _journalBytes + serialized.Length > options.MaximumJournalBytes)
            {
                Dropped.Add(1, new KeyValuePair<string, object?>("reason", "capacity"));
                return EventAppendStatus.CapacityReached;
            }

            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 16_384, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(serialized, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, destination, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            _eventCount++;
            _journalBytes += serialized.Length;
            Appended.Add(1, new KeyValuePair<string, object?>("outcome", "created"));
            return EventAppendStatus.Created;
        }
        finally { _gate.Release(); }
    }

    private async Task RecoverUnderLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.JournalDirectory);
        var result = new EventJournalRecoveryResult(0, 0, 0);
        var count = 0;
        var invalid = 0;
        long bytes = 0;
        foreach (var path in Directory.EnumerateFiles(options.JournalDirectory, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var envelope = JsonSerializer.Deserialize<EventJournalEnvelopeV1>(await File.ReadAllBytesAsync(path, cancellationToken), JsonOptions);
                if (!IsValid(envelope, path)) { invalid++; continue; }
                count++;
                bytes += new FileInfo(path).Length;
            }
            catch (Exception exception) when (exception is IOException or JsonException or ArgumentException) { invalid++; }
        }
        result = new EventJournalRecoveryResult(count, invalid, bytes);
        _eventCount = result.RecoveredEvents;
        _journalBytes = result.JournalBytes;
        _recovered = true;
    }

    private static bool IsValid(EventJournalEnvelopeV1? envelope, string path)
    {
        if (envelope is null || envelope.SchemaVersion != "1.0" || envelope.Event is null
            || !IsSafeIdentifier(envelope.Event.EventId) || !IsSafeIdentifier(envelope.Event.CameraId)
            || !IsSafeIdentifier(envelope.Event.StreamEpoch)
            || Path.GetFileNameWithoutExtension(path) != envelope.Event.EventId) return false;
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope.Event, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(hash),
            System.Text.Encoding.ASCII.GetBytes(envelope.EventSha256));
    }

    private static bool IsSafeIdentifier(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
