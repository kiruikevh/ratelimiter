using StackExchange.Redis;

namespace RateLimiter.Gateway.RateLimiting;

public sealed record BucketResult(bool Allowed, int Remaining, int RetryAfterMs, int ResetMs);


public sealed class RedisTokenBucket(IConnectionMultiplexer redis)
{

    private const string Script = """
        local key  = KEYS[1]
        local cap  = tonumber(ARGV[1])
        local rate = tonumber(ARGV[2])
        local cost = tonumber(ARGV[3])

        local t   = redis.call('TIME')
        local now = tonumber(t[1]) * 1000 + math.floor(tonumber(t[2]) / 1000)

        local b      = redis.call('HMGET', key, 'tokens', 'ts')
        local tokens = tonumber(b[1]) or cap
        local ts     = tonumber(b[2]) or now

        tokens = math.min(cap, tokens + (now - ts) / 1000 * rate)

        local allowed, retry = 0, 0
        if tokens >= cost then
          tokens  = tokens - cost
          allowed = 1
        else
          retry = math.ceil((cost - tokens) / rate * 1000)
        end

        redis.call('HSET', key, 'tokens', tostring(tokens), 'ts', tostring(now))
        redis.call('PEXPIRE', key, math.ceil(cap / rate * 1000) + 1000)

        local reset = math.ceil((cap - tokens) / rate * 1000)
        return { allowed, math.floor(tokens), retry, reset }
        """;

    public static string KeyFor(string clientId, string endpoint) => $"rl:{{{clientId}}}:{endpoint}";

    /// <summary>Spend <paramref name="cost"/> tokens if there are enough. Atomic across all gateways.</summary>
    public async Task<BucketResult> TryTakeAsync(string key, RateLimitRule rule, int cost = 1)
    {
        // ScriptEvaluateAsync sends EVALSHA after the first call, so the script text isn't
        // re-sent on every request.
        var raw = (RedisResult[])(await redis.GetDatabase().ScriptEvaluateAsync(
            Script,
            keys: [key],
            values: [rule.Capacity, rule.RefillRatePerSec, cost]))!;

        return new BucketResult(
            Allowed: (int)raw[0] == 1,
            Remaining: (int)raw[1],
            RetryAfterMs: (int)raw[2],
            ResetMs: (int)raw[3]);
    }

    /// <summary>For the /admin/bucket endpoint: what does the clicker currently say?</summary>
    public async Task<object> InspectAsync(string key)
    {
        var db = redis.GetDatabase();
        var fields = await db.HashGetAllAsync(key);
        var ttl = await db.KeyTimeToLiveAsync(key);
        return new
        {
            key,
            exists = fields.Length > 0,
            fields = fields.ToDictionary(f => f.Name.ToString(), f => f.Value.ToString()),
            ttlMs = ttl?.TotalMilliseconds
        };
    }
}
