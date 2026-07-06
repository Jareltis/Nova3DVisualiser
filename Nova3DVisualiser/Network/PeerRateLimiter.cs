using System.Collections.Generic;

namespace Nova3DVisualiser.Network;

/// <summary>
/// Per-key (connId) token bucket for fairness: each peer gets capacity <c>burst</c> that refills
/// <c>ratePerSec</c> tokens/second. <see cref="Allow"/> consumes one token if available (returns true),
/// else returns false so the caller drops that packet — so one flooder can't starve the others. Pure and
/// clock-injectable (<c>nowMs</c>) → unit-testable; thread-safe (the read loop runs on a background task).
/// Tracked keys are removed on disconnect via <see cref="Forget"/>, so they stay bounded by the live
/// connection count.
/// </summary>
public sealed class PeerRateLimiter
{
    private sealed class Bucket { public double Tokens; public long LastMs; }

    private readonly double _ratePerSec;
    private readonly double _burst;
    private readonly Dictionary<int, Bucket> _buckets = new();
    private readonly object _lock = new();

    public PeerRateLimiter(double ratePerSec, double burst)
    {
        _ratePerSec = ratePerSec;
        _burst = burst;
    }

    public bool Allow(int key, long nowMs)
    {
        lock (_lock)
        {
            if (!_buckets.TryGetValue(key, out var b))
            {
                b = new Bucket { Tokens = _burst, LastMs = nowMs };   // fresh peer starts with a full burst
                _buckets[key] = b;
            }
            else
            {
                double elapsedSec = (nowMs - b.LastMs) / 1000.0;      // refill by elapsed time, capped at burst
                if (elapsedSec > 0)
                {
                    b.Tokens = System.Math.Min(_burst, b.Tokens + elapsedSec * _ratePerSec);
                    b.LastMs = nowMs;
                }
            }

            if (b.Tokens >= 1.0) { b.Tokens -= 1.0; return true; }
            return false;   // over-rate → caller drops the packet (does NOT drop the connection)
        }
    }

    public void Forget(int key)
    {
        lock (_lock) { _buckets.Remove(key); }
    }
}
