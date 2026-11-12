using RateLimiter.Gateway.RateLimiting;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var redisOptions = ConfigurationOptions.Parse(builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379");
redisOptions.AbortOnConnectFail = false;
redisOptions.ConnectTimeout = 2000;
redisOptions.SyncTimeout = redisOptions.AsyncTimeout = 1000;
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));

// ── Rate limiter pieces ─────────────────────────────────────────────────────────
builder.Services.AddSingleton<IRulesStore, JsonFileRulesStore>();       // rulebook (Postgres in prod)
builder.Services.AddSingleton<RulesCache>();                             // memorised rulebook
builder.Services.AddHostedService(sp => sp.GetRequiredService<RulesCache>());
builder.Services.AddSingleton<RedisTokenBucket>();                       // the shared clicker
builder.Services.AddSingleton<LocalFallbackLimiter>();                   // pocket clicker
builder.Services.AddSingleton<RecentDenials>();
builder.Services.AddSingleton<RateLimitStats>();

var app = builder.Build();

// Load the rules once before taking traffic.
await app.Services.GetRequiredService<RulesCache>().RefreshAsync(CancellationToken.None);

// ── The bouncer stands at the door: every request passes through here first ────
app.UseMiddleware<RateLimitMiddleware>();


app.MapGet("/api/search", (string? q) => new { service = "search", query = q, results = new[] { "a", "b", "c" } });
app.MapGet("/api/orders", () => new { service = "orders", orders = new[] { 1001, 1002 } });
app.MapPost("/api/login", () => new { service = "auth", message = "login accepted (demo)" });

app.MapGet("/", () => Results.Redirect("/admin/rules"));
app.MapGet("/admin/rules", (RulesCache rules) => rules.All);
app.MapGet("/admin/stats", (RateLimitStats stats) => stats.Snapshot());

app.MapGet("/admin/bucket", async (string key, RedisTokenBucket bucket) =>
{
    try { return Results.Ok(await bucket.InspectAsync(key)); }
    catch (RedisException ex) { return Results.Problem($"Redis unavailable: {ex.Message}", statusCode: 503); }
});

app.MapGet("/admin/health", (IConnectionMultiplexer redis) => new
{
    redisConnected = redis.IsConnected,
    redisEndpoints = redis.GetEndPoints().Select(e => e.ToString())
});

app.Run();

public partial class Program;
