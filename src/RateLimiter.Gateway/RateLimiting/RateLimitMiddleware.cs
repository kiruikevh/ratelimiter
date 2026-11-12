using System.Globalization;
using StackExchange.Redis;

namespace RateLimiter.Gateway.RateLimiting;


public sealed class RateLimitMiddleware(
    RequestDelegate next,
    RulesCache rules,
    RedisTokenBucket redisBucket,
    LocalFallbackLimiter fallback,
    RecentDenials recentDenials,
    RateLimitStats stats,
    IConfiguration config,
    ILogger<RateLimitMiddleware> log)
{
    private readonly TimeSpan _redisTimeout =
        TimeSpan.FromMilliseconds(config.GetValue("RateLimiting:RedisTimeoutMs", 50));

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);  
            return;
        }

        // 1. Who is this guest?
        var (scope, clientId, tier) = IdentifyClient(ctx);

        var rule = rules.Match(scope, tier, path);
        if (rule is null)
        {
            await next(ctx);
            return;
        }

        var key = RedisTokenBucket.KeyFor(clientId, rule.Endpoint);
        ctx.Response.Headers["X-RateLimit-Rule"] = rule.Id.ToString(CultureInfo.InvariantCulture);
        ctx.Response.Headers["X-RateLimit-Key"] = key;

        // 3. Turned away a moment ago? Don't even bother Redis.
        if (recentDenials.IsDenied(key, out var stillBannedMs))
        {
            Interlocked.Increment(ref stats.ThrottledLocally);
            await Reject(ctx, rule, stillBannedMs, source: "local-precheck");
            return;
        }

        // 4. Ask the shared clicker in Redis.
        BucketResult result;
        string source;
        try
        {
            result = await redisBucket.TryTakeAsync(key, rule).WaitAsync(_redisTimeout);
            source = "redis";
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // 6. Redis is down or slow. Each rule decides what happens.
            if (rule.FailsClosed)
            {
                Interlocked.Increment(ref stats.FailClosed);
                log.LogError("Redis unavailable, rule {Rule} fails CLOSED → 503 for {Key}", rule.Id, key);
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.Headers.RetryAfter = "5";
                ctx.Response.Headers["X-RateLimit-Source"] = "fail-closed";
                await ctx.Response.WriteAsJsonAsync(new { error = "Rate limiter unavailable; sensitive endpoint is closed." });
                return;
            }

            Interlocked.Increment(ref stats.FailOpen);
            log.LogWarning("Redis unavailable ({Error}), rule {Rule} fails OPEN → local limiter for {Key}",
                ex.GetType().Name, rule.Id, key);
            result = fallback.TryTake(key, rule);
            source = "local-fallback";
        }

        if (!result.Allowed)
        {
            Interlocked.Increment(ref stats.Throttled);
            recentDenials.Remember(key, result.RetryAfterMs);
            log.LogInformation("429 {Key} rule {Rule} retry in {Ms} ms", key, rule.Id, result.RetryAfterMs);
            await Reject(ctx, rule, result.RetryAfterMs, source);
            return;
        }

        // 5. Allowed: tell the client where they stand, then let them into the club.
        Interlocked.Increment(ref stats.Allowed);
        WriteHeaders(ctx, rule, result.Remaining, result.ResetMs, source);
        await next(ctx);
    }

    /// <summary>
    /// API key if present and known (tier comes from config), otherwise the caller's IP.
    /// In production the tier would come from your auth/user service.
    /// </summary>
    private (string Scope, string ClientId, string Tier) IdentifyClient(HttpContext ctx)
    {
        var apiKey = ctx.Request.Headers["X-Api-Key"].ToString();
        if (!string.IsNullOrEmpty(apiKey))
        {
            var tier = config[$"ApiKeys:{apiKey}"];
            if (tier is not null) return ("api_key", $"api_key:{apiKey}", tier);
        }

        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return ("ip", $"ip:{ip}", "*");
    }

    private static void WriteHeaders(HttpContext ctx, RateLimitRule rule, int remaining, int resetMs, string source)
    {
        var h = ctx.Response.Headers;
        h["X-RateLimit-Limit"] = rule.Capacity.ToString(CultureInfo.InvariantCulture);
        h["X-RateLimit-Remaining"] = remaining.ToString(CultureInfo.InvariantCulture);
        h["X-RateLimit-Reset"] = Math.Ceiling(resetMs / 1000.0).ToString(CultureInfo.InvariantCulture);
        h["X-RateLimit-Source"] = source;
    }

    private static async Task Reject(HttpContext ctx, RateLimitRule rule, int retryAfterMs, string source)
    {
        var retrySeconds = Math.Max(1, (int)Math.Ceiling(retryAfterMs / 1000.0));
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.Response.Headers.RetryAfter = retrySeconds.ToString(CultureInfo.InvariantCulture);
        WriteHeaders(ctx, rule, 0, retryAfterMs, source);
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = "Too many requests",
            retryAfterSeconds = retrySeconds,
            rule = rule.ToString()
        });
    }
}
