namespace RateLimiter.Gateway.RateLimiting;


public sealed record RateLimitRule
{
    public long Id { get; init; }


    public string Scope { get; init; } = "ip";

    public string Tier { get; init; } = "*";

    public string Endpoint { get; init; } = "*";

    public int Capacity { get; init; }


    public double RefillRatePerSec { get; init; }


    public string FailMode { get; init; } = "open";

    public bool FailsClosed => string.Equals(FailMode, "closed", StringComparison.OrdinalIgnoreCase);


    public override string ToString() =>
        $"#{Id} {Scope}/{Tier} {Endpoint} cap={Capacity} refill={RefillRatePerSec}/s fail={FailMode}";
}
