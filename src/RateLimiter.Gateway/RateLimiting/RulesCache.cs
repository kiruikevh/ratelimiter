namespace RateLimiter.Gateway.RateLimiting;

/// <summary>
/// The bouncer memorises the rulebook instead of running back to the office for every guest.
///
/// Rules are held in this gateway's memory and refreshed in the background, so matching a
/// request to a rule costs no network call. If the rules store is down, the last good copy
/// keeps being used (README, Step 12: "Rules DB down → no impact on traffic").
/// </summary>
public sealed class RulesCache(IRulesStore store, IConfiguration config, ILogger<RulesCache> log)
    : BackgroundService
{
    private volatile IReadOnlyList<RateLimitRule> _rules = [];

    public IReadOnlyList<RateLimitRule> All => _rules;

    /// <summary>
    /// Pick the most specific rule for this client and path:
    /// exact tier beats "*", a longer endpoint prefix beats a shorter one or "*".
    /// </summary>
    public RateLimitRule? Match(string scope, string tier, string path) =>
        _rules
            .Where(r => r.Scope == scope)
            .Where(r => r.Tier == "*" || r.Tier == tier)
            .Where(r => r.Endpoint == "*" || path.StartsWith(r.Endpoint, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Endpoint == "*" ? 0 : r.Endpoint.Length)
            .ThenByDescending(r => r.Tier == "*" ? 0 : 1)
            .FirstOrDefault();

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var fresh = await store.LoadAllAsync(ct);
            if (fresh.Count > 0) _rules = fresh;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Rules store unavailable; keeping {Count} cached rules", _rules.Count);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var every = TimeSpan.FromSeconds(config.GetValue("RateLimiting:RulesRefreshSeconds", 10));
        using var timer = new PeriodicTimer(every);
        while (await timer.WaitForNextTickAsync(ct))
            await RefreshAsync(ct);
    }
}
