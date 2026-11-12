using System.Text.Json;

namespace RateLimiter.Gateway.RateLimiting;

/// <summary>
/// Where the rulebook lives (the cold path). Durable, rarely changed, edited by admins.
/// </summary>
public interface IRulesStore
{
    Task<IReadOnlyList<RateLimitRule>> LoadAllAsync(CancellationToken ct);
}


public sealed class JsonFileRulesStore(IWebHostEnvironment env, ILogger<JsonFileRulesStore> log) : IRulesStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<RateLimitRule>> LoadAllAsync(CancellationToken ct)
    {
        var path = Path.Combine(env.ContentRootPath, "rules.json");
        await using var stream = File.OpenRead(path);
        var rules = await JsonSerializer.DeserializeAsync<List<RateLimitRule>>(stream, Json, ct) ?? [];
        log.LogDebug("Loaded {Count} rules from {Path}", rules.Count, path);
        return rules;
    }
}
