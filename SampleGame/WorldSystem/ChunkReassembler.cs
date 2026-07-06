using System.Collections.Generic;

namespace SampleGame.Worlds;

/// <summary>Result of feeding one chunk to a <see cref="ChunkReassembler{TPart}"/>.</summary>
public enum ChunkOutcome { Pending, Completed, Rejected }

/// <summary>
/// Bounds the client-side reassembly of a chunked stream (mesh .obj text / texture PNG bytes) against a
/// hostile peer. Without these caps, an attacker-controlled <c>Total</c> forces <c>new TPart[2_000_000_000]</c>
/// (OOM), and an unbounded number of distinct in-flight names / total buffered bytes / never-completed
/// reassemblies grows memory without limit. Pure — no I/O — so it is deterministic and unit-testable
/// (<c>securitytest</c>). A legitimate multi-part stream reassembles identically (same parts, same order).
///
/// Single-threaded by contract (the handlers run on the main thread via ProcessEvents) — no locks.
/// </summary>
public sealed class ChunkReassembler<TPart>
{
    private sealed class Entry
    {
        public TPart[] Parts;
        public long[] SlotBytes;   // bytes recorded per slot, so a duplicate-index overwrite adjusts by delta
        public int Total;
        public int Received;       // count of non-null slots (== Total ⇒ complete)
        public long BufferedBytes;
        public long Touch;         // last-touched stamp for LRU eviction

        public Entry(int total) { Parts = new TPart[total]; SlotBytes = new long[total]; Total = total; }
    }

    private readonly int _maxParts;
    private readonly int _maxConcurrent;
    private readonly long _maxBufferedBytes;

    private readonly Dictionary<string, Entry> _entries = new();
    private long _touch;                // monotonic LRU counter
    private long _totalBufferedBytes;   // running sum across ALL entries

    public ChunkReassembler(int maxParts, int maxConcurrent, long maxBufferedBytes)
    {
        _maxParts = maxParts;
        _maxConcurrent = maxConcurrent;
        _maxBufferedBytes = maxBufferedBytes;
    }

    /// <summary>Total bytes currently buffered across all in-flight reassemblies (for tests/diagnostics).</summary>
    public long TotalBufferedBytes => _totalBufferedBytes;
    /// <summary>Number of distinct in-flight reassemblies (for tests/diagnostics).</summary>
    public int ActiveCount => _entries.Count;

    /// <summary>
    /// Accept one chunk. Returns <see cref="ChunkOutcome.Rejected"/> (nothing stored) when <paramref name="total"/>
    /// is out of range or <paramref name="index"/> is out of range or a single part can't fit the byte cap;
    /// <see cref="ChunkOutcome.Completed"/> with the ordered <paramref name="completed"/> parts when the full set
    /// has arrived; otherwise <see cref="ChunkOutcome.Pending"/>.
    /// </summary>
    public ChunkOutcome Accept(string name, int index, int total, TPart part, int partBytes,
                               out TPart[]? completed, out string? rejectReason)
    {
        completed = null;
        rejectReason = null;

        // Reject BEFORE any allocation.
        if (total <= 0 || total > _maxParts) { rejectReason = $"total {total} out of range (max {_maxParts})"; return ChunkOutcome.Rejected; }
        if (index < 0 || index >= total) { rejectReason = $"index {index} out of range for total {total}"; return ChunkOutcome.Rejected; }

        _entries.TryGetValue(name, out var entry);

        // An existing entry whose stored total differs → restart (drop the stale partial).
        if (entry != null && entry.Total != total)
        {
            _totalBufferedBytes -= entry.BufferedBytes;
            _entries.Remove(name);
            entry = null;
        }

        // New name (or just-restarted): make room under the concurrency cap, then allocate (total ≤ maxParts).
        if (entry == null)
        {
            while (_entries.Count >= _maxConcurrent && TryEvictLowestTouch(exclude: null)) { }
            entry = new Entry(total);
            _entries[name] = entry;
        }

        // Byte cap: evict LRU OTHER reassemblies until this part fits; if it still can't (a single part
        // exceeds the cap), drop this entry and reject.
        while (_totalBufferedBytes + partBytes > _maxBufferedBytes && TryEvictLowestTouch(exclude: name)) { }
        if (_totalBufferedBytes + partBytes > _maxBufferedBytes)
        {
            _totalBufferedBytes -= entry.BufferedBytes;
            _entries.Remove(name);
            rejectReason = $"buffered bytes would exceed cap {_maxBufferedBytes}";
            return ChunkOutcome.Rejected;
        }

        // Store the part (adjust byte accounting by delta so a duplicate index doesn't double-count).
        long oldBytes = entry.SlotBytes[index];
        bool wasFilled = entry.Parts[index] is not null;
        entry.Parts[index] = part;
        entry.SlotBytes[index] = partBytes;
        long delta = partBytes - oldBytes;
        entry.BufferedBytes += delta;
        _totalBufferedBytes += delta;

        bool nowFilled = part is not null;
        if (nowFilled && !wasFilled) entry.Received++;
        else if (!nowFilled && wasFilled) entry.Received--;

        entry.Touch = ++_touch;

        if (entry.Received == entry.Total)
        {
            _totalBufferedBytes -= entry.BufferedBytes;
            _entries.Remove(name);
            completed = entry.Parts;
            return ChunkOutcome.Completed;
        }
        return ChunkOutcome.Pending;
    }

    // Evicts the lowest-Touch entry (LRU), excluding `exclude`. Returns false if there is nothing to evict.
    private bool TryEvictLowestTouch(string? exclude)
    {
        string? victim = null;
        long lowest = long.MaxValue;
        foreach (var kv in _entries)
        {
            if (kv.Key == exclude) continue;
            if (kv.Value.Touch < lowest) { lowest = kv.Value.Touch; victim = kv.Key; }
        }
        if (victim == null) return false;
        _totalBufferedBytes -= _entries[victim].BufferedBytes;
        _entries.Remove(victim);
        return true;
    }
}
