using System.Collections.Generic;

namespace Nova3DVisualiser.Logging;

/// <summary>
/// Per COARSE key (a category, not the variable detail), allow the first occurrence, then suppress
/// duplicates for a cooldown window; when the window elapses, allow one and report how many were
/// suppressed meanwhile. Bounds the number of tracked keys so a unique-message flood can't grow memory.
/// Deterministic with an injected millisecond clock (<c>nowMs</c>), so it is unit-testable; thread-safe
/// (the anomaly path is hit from background read loops).
/// </summary>
public sealed class LogRateLimiter
{
    private sealed class State { public long LastEmitMs; public int Suppressed; }

    public const int MaxTrackedKeys = 64;

    private readonly long _cooldownMs;
    private readonly Dictionary<string, State> _keys = new();
    private readonly object _lock = new();

    public LogRateLimiter(long cooldownMs = 5000) { _cooldownMs = cooldownMs; }

    public long CooldownMs => _cooldownMs;

    /// <summary>
    /// Returns true if this occurrence of <paramref name="key"/> should be logged now. On a true return,
    /// <paramref name="suppressedSinceLast"/> is how many occurrences were dropped since the previous emit
    /// (0 for the first). On false, the occurrence was within the cooldown and has been counted as suppressed.
    /// </summary>
    public bool ShouldLog(string key, long nowMs, out int suppressedSinceLast)
    {
        suppressedSinceLast = 0;
        lock (_lock)
        {
            if (_keys.TryGetValue(key, out var st))
            {
                if (nowMs - st.LastEmitMs < _cooldownMs) { st.Suppressed++; return false; }   // within cooldown → drop
                suppressedSinceLast = st.Suppressed;   // window elapsed → emit, report + reset
                st.Suppressed = 0;
                st.LastEmitMs = nowMs;
                return true;
            }

            // New key. At the cap, allow it (a real anomaly is never silenced) but don't track it — rotation
            // still bounds disk, and this prevents unbounded dictionary growth under a unique-message flood.
            if (_keys.Count >= MaxTrackedKeys) return true;

            _keys[key] = new State { LastEmitMs = nowMs, Suppressed = 0 };
            return true;
        }
    }
}
