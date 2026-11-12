# Walks through the rate limiter step by step. Start Redis and the gateway first:
#   docker compose up -d
#   dotnet run --project src/RateLimiter.Gateway
param([string]$Gateway = "http://localhost:5080")

function Show-Call([string]$Method, [string]$Path, [hashtable]$Headers = @{}) {
    try {
        $r = Invoke-WebRequest -Uri "$Gateway$Path" -Method $Method -Headers $Headers -UseBasicParsing
    } catch [System.Net.WebException] {
        $r = $_.Exception.Response
    }
    $h = $r.Headers
    "{0,-4} {1,-14} remaining={2,-3} source={3,-15} retry-after={4}" -f `
        [int]$r.StatusCode, $Path, $h["X-RateLimit-Remaining"], $h["X-RateLimit-Source"], $h["Retry-After"]
}

Write-Host "`n1) Is Redis connected?" -ForegroundColor Cyan
Invoke-RestMethod "$Gateway/admin/health" | Format-List

Write-Host "2) Free key hits /api/search 7 times (rule #3: bucket of 5, refills 1 token / 5 s)" -ForegroundColor Cyan
1..7 | ForEach-Object { Show-Call GET "/api/search" @{ "X-Api-Key" = "free-key-123" } }

Write-Host "`n3) What did the limiter store in Redis for that client?" -ForegroundColor Cyan
$key = [uri]::EscapeDataString("rl:{api_key:free-key-123}:/api/search")
try { Invoke-RestMethod "$Gateway/admin/bucket?key=$key" | ConvertTo-Json }
catch { Write-Host "   (Redis is down, so there's nothing to inspect. The gateway is using its local fallback.)" -ForegroundColor Yellow }

Write-Host "`n4) Same free key on /api/orders uses a DIFFERENT bucket (rule #1: 10 per burst)" -ForegroundColor Cyan
1..3 | ForEach-Object { Show-Call GET "/api/orders" @{ "X-Api-Key" = "free-key-123" } }

Write-Host "`n5) Pro key gets a much bigger bucket (rule #2: 100)" -ForegroundColor Cyan
1..3 | ForEach-Object { Show-Call GET "/api/search" @{ "X-Api-Key" = "pro-key-456" } }

Write-Host "`n6) Login is limited per IP (rule #4: 5, fails CLOSED if Redis dies)" -ForegroundColor Cyan
1..6 | ForEach-Object { Show-Call POST "/api/login" }

Write-Host "`n7) Counters for monitoring" -ForegroundColor Cyan
Invoke-RestMethod "$Gateway/admin/stats" | Format-List
