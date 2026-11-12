

using System.Collections.Concurrent;
using System.Diagnostics;

var total = args.Length > 0 ? int.Parse(args[0]) : 50;
var apiKey = args.Length > 1 ? args[1] : "free-key-123";
var path = args.Length > 2 ? args[2] : "/api/search";
var gateways = (args.Length > 3 ? args[3] : "http://localhost:5080")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

using var http = new HttpClient();
var statusCounts = new ConcurrentDictionary<string, int>();
var sources = new ConcurrentDictionary<string, int>();
var perGateway = new ConcurrentDictionary<string, int>();

Console.WriteLine($"Firing {total} concurrent requests at {path} with X-Api-Key={apiKey}");
Console.WriteLine($"Gateways: {string.Join(", ", gateways)}\n");

var sw = Stopwatch.StartNew();
var tasks = Enumerable.Range(0, total).Select(async i =>
{
    var gateway = gateways[i % gateways.Length];
    using var req = new HttpRequestMessage(HttpMethod.Get, gateway + path);
    req.Headers.Add("X-Api-Key", apiKey);
    try
    {
        using var res = await http.SendAsync(req);
        var code = ((int)res.StatusCode).ToString();
        statusCounts.AddOrUpdate(code, 1, (_, n) => n + 1);
        if (res.Headers.TryGetValues("X-RateLimit-Source", out var src))
            sources.AddOrUpdate(src.First(), 1, (_, n) => n + 1);
        if (res.IsSuccessStatusCode)
            perGateway.AddOrUpdate(gateway, 1, (_, n) => n + 1);
    }
    catch (HttpRequestException ex)
    {
        statusCounts.AddOrUpdate($"error ({ex.Message})", 1, (_, n) => n + 1);
    }
});
await Task.WhenAll(tasks);
sw.Stop();

Console.WriteLine("Status codes:");
foreach (var (code, n) in statusCounts.OrderBy(kv => kv.Key))
    Console.WriteLine($"  {code,-6} x {n}");

Console.WriteLine("\nDecided by (X-RateLimit-Source):");
foreach (var (src, n) in sources.OrderBy(kv => kv.Key))
    Console.WriteLine($"  {src,-16} x {n}");

Console.WriteLine("\nAllowed per gateway:");
foreach (var gw in gateways)
    Console.WriteLine($"  {gw,-24} {perGateway.GetValueOrDefault(gw)}");

Console.WriteLine($"\nTotal allowed: {perGateway.Values.Sum()}  in {sw.ElapsedMilliseconds} ms");
