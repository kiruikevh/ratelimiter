using System.Collections.Concurrent;

namespace RateLimiter.Gateway.RateLimiting;


public sealed class LocalFallbackLimiter
{
    private sealed class Bucket { public double Tokens; public long LastTicks; }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    public BucketResult TryTake(string key, RateLimitRule rule, int cost = 1)
    {
        var b = _buckets.GetOrAdd(key, _ => new Bucket { Tokens = rule.Capacity, LastTicks = Environment.TickCount64 });
        lock (b)
        {
            var now = Environment.TickCount64;
            b.Tokens = Math.Min(rule.Capacity, b.Tokens + (now - b.LastTicks) / 1000.0 * rule.RefillRatePerSec);
            b.LastTicks = now;

            if (b.Tokens >= cost)
            {
                b.Tokens -= cost;
                return new BucketResult(true, (int)b.Tokens, 0,
                    (int)Math.Ceiling((rule.Capacity - b.Tokens) / rule.RefillRatePerSec * 1000));
            }

            var retry = (int)Math.Ceiling((cost - b.Tokens) / rule.RefillRatePerSec * 1000);
            return new BucketResult(false, 0, retry,
                (int)Math.Ceiling((rule.Capacity - b.Tokens) / rule.RefillRatePerSec * 1000));
        }
    }
}


public sealed class RecentDenials
{
    private readonly ConcurrentDictionary<string, long> _deniedUntil = new();

    public void Remember(string key, int retryAfterMs) =>
        _deniedUntil[key] = Environment.TickCount64 + retryAfterMs;

    public bool IsDenied(string key, out int retryAfterMs)
    {
        retryAfterMs = 0;
        if (!_deniedUntil.TryGetValue(key, out var until)) return false;
        var left = until - Environment.TickCount64;
        if (left <= 0) { _deniedUntil.TryRemove(key, out _); return false; }
        retryAfterMs = (int)left;
        return true;
    }
}

/// <summary>Counters for monitoring (README, Step 13). Real systems export these to Prometheus.</summary>
public sealed class RateLimitStats
{
    public long Allowed, Throttled, ThrottledLocally, FailOpen, FailClosed;

    public object Snapshot() => new
    {
        allowed = Interlocked.Read(ref Allowed),
        throttled = Interlocked.Read(ref Throttled),
        throttledLocallyWithoutCallingRedis = Interlocked.Read(ref ThrottledLocally),
        redisDownFailOpen = Interlocked.Read(ref FailOpen),
        redisDownFailClosed = Interlocked.Read(ref FailClosed)
    };
}
