# API Rate Limiter in C# with Redis

A working version of the "nightclub bouncer" rate limiter design: an ASP.NET Core gateway
that rate-limits every request with a token bucket held in Redis.

| Analogy             | In this code                                                                 |
|---------------------|------------------------------------------------------------------------------|
| The club            | The `/api/*` endpoints in `Program.cs` (they contain no rate-limit code)     |
| The bouncer         | `RateLimitMiddleware.cs`, which runs before every request                    |
| The rulebook        | `rules.json` through `JsonFileRulesStore` (Postgres in production)           |
| Memorised rulebook  | `RulesCache.cs`, kept in memory and refreshed every 10 s                     |
| **The clicker**     | **Redis, through `RedisTokenBucket.cs` (atomic Lua script)**                 |
| Pocket clicker      | `LocalFallbackLimiter.cs`, used only when Redis is down and the rule fails open |
| "Come back later"   | `429 Too Many Requests` + `Retry-After`                                      |

```
                       ┌──────────────── Gateway (this app) ─────────────────┐
Client ── X-Api-Key ──▶│ RateLimitMiddleware                                 │
                       │   1. identify client  → api_key:free-key-123        │
                       │   2. RulesCache.Match → rule #3 (memory, no I/O)    │
                       │   3. RecentDenials?   → deny without calling Redis  │
                       │   4. RedisTokenBucket ──── EVALSHA ───────────────┐ │
                       │   5a. allowed → next() → /api/search  (200)       │ │
                       │   5b. denied  → 429 + Retry-After                 │ │
                       └───────────────────────────────────────────────────┼─┘
                                                                           ▼
                                          ┌────────────────────────────────────┐
     other gateway instances ───────────▶ │ Redis                              │
     (all share the same count)           │ rl:{api_key:free-key-123}:/api/search
                                          │   tokens = "2.4"  ts = "17313…"    │
                                          │   TTL ≈ 26 s                       │
                                          └────────────────────────────────────┘
```

## Where Redis comes in

Redis is used for **one thing only**: storing each client's token bucket so that every
gateway instance sees the same count. All of it is in
[`RedisTokenBucket.cs`](src/RateLimiter.Gateway/RateLimiting/RedisTokenBucket.cs).

**1. One small hash per client per rule.**

```
key:   rl:{api_key:free-key-123}:/api/search
value: tokens = "2.4", ts = "1731320400123"
TTL:   time to refill completely + 1 s
```

That's roughly 100 bytes per active client. With 1M active clients it comes to about 100 MB of RAM.
The `{...}` is a Redis Cluster hash tag, so all of one client's keys land on the same shard.
The TTL means idle clients clean up after themselves, so you never need a cleanup job.

**2. One atomic Lua script per request.** The middleware never does
`GET` → compute in C# → `SET`. With two gateways that would race: both read `tokens = 1`
and both let a request in. Instead, the whole sequence runs inside Redis:

```lua
now    = redis TIME                           -- Redis's clock, not the gateway's
tokens = min(cap, tokens + elapsed * rate)    -- refill
if tokens >= cost then tokens -= cost; allowed = 1 else retry = ... end
HSET + PEXPIRE
return {allowed, remaining, retryMs, resetMs}
```

Redis runs scripts one at a time, so it's atomic, and it takes one network round trip.
StackExchange.Redis sends `EVALSHA` after the first call, so the script text isn't
re-sent with every request.

**3. Redis is never trusted to be up.** The call has a hard timeout (`RedisTimeoutMs`, 50 ms
here; aim for about 5 ms in production on the same network). If Redis is slow or down:

| Rule `failMode`         | What happens                                                                 |
|-------------------------|------------------------------------------------------------------------------|
| `open` (most endpoints) | Use `LocalFallbackLimiter`, an in-memory bucket per gateway. Coarse, but still some protection |
| `closed` (`/api/login`) | Return `503`. Better to block logins than allow unlimited password guessing  |

**4. Redis is protected from abusers.** After a 429, `RecentDenials` remembers the key
until its `Retry-After` expires, and further requests are rejected in memory without calling Redis.

## Rules (`src/RateLimiter.Gateway/rules.json`)

| # | Scope   | Tier | Endpoint      | Capacity | Refill     | Fail   |
|---|---------|------|---------------|----------|------------|--------|
| 1 | api_key | free | *             | 10       | 0.5 / s    | open   |
| 2 | api_key | pro  | *             | 100      | 10 / s     | open   |
| 3 | api_key | free | /api/search   | 5        | 0.2 / s    | open   |
| 4 | ip      | *    | /api/login    | 5        | ~5 / min   | closed |
| 5 | ip      | *    | *             | 20       | 1 / s      | open   |

The most specific rule wins: a longer endpoint prefix beats a shorter one, and an exact tier beats `*`.
API keys and their tiers are in `appsettings.json` (`free-key-123`, `pro-key-456`).
Requests without a known key are limited by IP.

**Change rules without redeploying:** edit `rules.json` while the gateway is running. Every
gateway picks up the change within `RulesRefreshSeconds` (10 s). In production, this is an
`UPDATE` on the Postgres `rate_limit_rules` table.

## Running it

**1. Start Redis.** Use one of these:

```powershell
docker compose up -d                         # Docker Desktop
# or install Memurai (Redis-compatible for Windows) and start it on port 6379
# or in WSL:  sudo apt install redis-server && redis-server
```

**2. Start the gateway.**

```powershell
dotnet run --project src/RateLimiter.Gateway        # http://localhost:5080
```

**3. Walk through the demo.**

```powershell
.\demo.ps1
```

**4. Watch Redis while you send requests** (in another terminal):

```powershell
docker exec -it ratelimiter-redis redis-cli MONITOR
# you'll see one EVALSHA per request, then:
docker exec -it ratelimiter-redis redis-cli HGETALL "rl:{api_key:free-key-123}:/api/search"
docker exec -it ratelimiter-redis redis-cli PTTL    "rl:{api_key:free-key-123}:/api/search"
```

**5. Show that many doors share one count and that there's no race.** Run a second gateway on another port,
then fire 50 simultaneous requests split across both:

```powershell
dotnet run --project src/RateLimiter.Gateway --urls http://localhost:5081
dotnet run --project src/RateLimiter.LoadTest -- 50 free-key-123 /api/search http://localhost:5080,http://localhost:5081
```

The total allowed is about 5 across both gateways, not 5 each. To see the alternative,
stop Redis and run it again: each gateway falls back to its own local count, so about 10 get through. That's the
trade-off the fail-open mode accepts.

**6. Kill Redis while it's running** (`docker stop ratelimiter-redis`). `/api/search` keeps
working with `X-RateLimit-Source: local-fallback`, and `/api/login` returns `503`. Start Redis
again and the source switches back to `redis` automatically.

## Response headers

| Header                  | Meaning                                                                |
|-------------------------|------------------------------------------------------------------------|
| `X-RateLimit-Limit`     | Bucket capacity                                                        |
| `X-RateLimit-Remaining` | Tokens left                                                            |
| `X-RateLimit-Reset`     | Seconds until the bucket is full again                                 |
| `Retry-After`           | On 429, seconds until one token is available                           |
| `X-RateLimit-Source`    | `redis`, `local-precheck`, `local-fallback` or `fail-closed` (for learning) |
| `X-RateLimit-Rule/Key`  | Which rule matched and the Redis key used (for learning)               |

## Admin endpoints (not rate limited)

- `GET /admin/health`: is Redis connected?
- `GET /admin/rules`: the rules this gateway currently has cached
- `GET /admin/stats`: allowed, throttled, fail-open and fail-closed counts (export to Prometheus in production)
- `GET /admin/bucket?key=...`: the raw Redis hash and TTL for one client

## What's simplified compared with production

- The backends are in-process endpoints. A real gateway would proxy to separate services, for example with YARP, or you'd use Kong, Envoy or NGINX with a Redis policy.
- The rules come from a JSON file instead of Postgres. `IRulesStore` is the only thing you'd swap.
- Tiers come from `appsettings.json` instead of an auth service.
- Metrics are in-memory counters instead of Prometheus or Kafka.
- It uses a single Redis node. For production, use Redis Cluster (3–6 shards with replicas). The hash-tagged keys are already cluster-ready.
