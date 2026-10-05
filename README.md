# Hacker News Best Stories API

An ASP.NET Core API that returns the best *n* Hacker News stories, sorted by score (highest first). It can handle large numbers of requests without increasing the load on Hacker News.

## Running it

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/HackerNews.Api
curl "http://localhost:5100/api/stories/best?n=10"
```

Run the tests (they do not call the real Hacker News API):

```bash
dotnet test
```

Interactive API docs (Swagger UI) are at [`/swagger`](http://localhost:5100/swagger); `/` redirects there. The OpenAPI document they render is at `/openapi/v1.json`.

## API

### `GET /api/stories/best?n={n}`

```json
[
  {
    "title": "A uBlock Origin update was rejected from the Chrome Web Store",
    "uri": "https://github.com/uBlockOrigin/uBlock-issues/issues/745",
    "postedBy": "ismaildonmez",
    "time": "2019-10-12T13:43:01+00:00",
    "score": 1716,
    "commentCount": 572
  }
]
```

| Status | When |
| --- | --- |
| `200 OK` | An array of up to `n` stories, sorted by score (highest first). Includes `ETag`, `Last-Modified` and `Cache-Control: public, max-age=30`. |
| `304 Not Modified` | The `If-None-Match` header matches the current data. The response has no body. |
| `400 Bad Request` | `n` is missing, is not a whole number, or is less than 1. |
| `429 Too Many Requests` | This client went over its rate limit. Includes `Retry-After`. |
| `503 Service Unavailable` | Hacker News could not be reached and nothing is cached yet. Includes `Retry-After`. |

All errors use [RFC 9457 problem details](https://www.rfc-editor.org/rfc/rfc9457) (`application/problem+json`).

### Health

| Endpoint | Meaning |
| --- | --- |
| `GET /health/live` | The process is running. |
| `GET /health/ready` | The stories have been loaded: `200 Healthy`, or `200 Degraded` if the data is older than `StaleAfter`. Returns `503 Unhealthy` before the first load. The JSON body includes the story count and the data's age. These probes only read the cache and never call Hacker News. |

## How Hacker News is protected

`beststories.json` returns up to about 200 IDs. Their order is not documented, so the API reads every story and sorts by score itself. Doing that on every request would mean about 200 calls to Hacker News per request. Instead, everything is served from an in-memory snapshot ([`BestStoriesCache`](src/HackerNews.Api/Stories/BestStoriesCache.cs)):

- **Calls to Hacker News don't grow with traffic.** The snapshot is refreshed at most once per `CacheDuration` (default 60s). The load on Hacker News is the same whether the API gets one request or a million.
- **Only one refresh runs at a time.** Concurrent requests share the refresh that is already running. If there are no requests, there are no refreshes.
- **Requests don't wait once data is loaded.** When the snapshot is out of date, requests get it immediately and a single refresh runs in the background.
- **The first refresh runs at startup.** A hosted service loads the stories when the app starts, without blocking startup. `/health/ready` stays unhealthy until they are loaded, so a load balancer does not route traffic to an empty instance.
- **One failed item doesn't throw away the rest.** If a story's request fails, the version from the previous refresh is used. The refresh as a whole fails only if more than `MaxItemFailureRatio` (default 10%) of item requests fail.
- **The API keeps working if Hacker News fails.** It keeps serving the last good snapshot and waits `RetryAfterFailure` (default 15s) before trying again. Requests return `503` only if nothing has been loaded yet.
- **Requests to Hacker News are limited and protected.** A refresh never runs more than `MaxConcurrentRequests` (default 8) item requests at once. The HTTP client uses .NET's standard resilience pipeline: attempt and total timeouts, retries with jittered backoff, and a circuit breaker. It sends a descriptive `User-Agent`.
- **Clients and CDNs can cache responses.** The `ETag` is a hash of the snapshot's content, so every instance that loaded the same data returns the same `ETag`. `If-None-Match` gets a `304`.
- **The API protects itself.** A per-IP fixed-window rate limiter (default 100 requests per 10s) stops one client from taking all the capacity.
- **Shutdown is clean.** Stopping the host cancels any refresh that is still running.

**Measured locally** (Debug build, Apple Silicon): `ab -n 20000 -c 100` → about 35,000 requests per second, 0 failures, p99 of 5 ms, and **one** refresh from Hacker News.

### Observability

- Structured logs use source-generated `LoggerMessage` methods.
- Meter `HackerNews.Api.BestStories` has these instruments:
  - `hackernews.refreshes`: refresh count, tagged `result=success|failure`.
  - `hackernews.refresh.duration`: refresh duration.
  - `hackernews.item.failures`: failed item requests.

  Watch them with `dotnet-counters monitor -n HackerNews.Api --counters HackerNews.Api.BestStories`, or export them with OpenTelemetry.

## Configuration

Everything is in [`appsettings.json`](src/HackerNews.Api/appsettings.json). It is validated at startup, so an invalid value stops the app instead of misbehaving later. Any setting can be overridden with an environment variable, e.g. `HackerNews__CacheDuration=00:00:30`.

| Setting | Default | Purpose |
| --- | --- | --- |
| `HackerNews:CacheDuration` | `00:01:00` | How long a snapshot is served before it is refreshed. |
| `HackerNews:RetryAfterFailure` | `00:00:15` | How long to wait after a failed refresh before trying again. |
| `HackerNews:MaxConcurrentRequests` | `8` | Maximum item requests in flight during a refresh. |
| `HackerNews:MaxItemFailureRatio` | `0.1` | Share of failed item requests a refresh tolerates. |
| `HackerNews:WarmUpOnStartup` | `true` | Load the stories when the app starts. |
| `HackerNews:ClientCacheDuration` | `00:00:30` | `Cache-Control: max-age` sent to clients. |
| `HackerNews:StaleAfter` | `00:10:00` | Data older than this makes the readiness check report `Degraded`. |
| `HackerNews:Resilience:*` | see file | [Standard resilience options](https://learn.microsoft.com/dotnet/core/resilience/http-resilience) for the HTTP client. |
| `RateLimiting:PermitLimit` / `Window` | `100` / `00:00:10` | Per-client rate limit. |

## Project layout

```
src/HackerNews.Api
  HackerNews/       Hacker News API client, its wire model and options
  Stories/          The feature: snapshot cache, endpoint, warm-up, health check, metrics
  Infrastructure/   Service registration, rate limiting, health check JSON writer
tests/HackerNews.Api.Tests
  BestStoriesCacheTests          Unit tests for the cache: ordering, single refresh, stale-while-revalidate, back-off, partial failures, shutdown
  BestStoriesEndpointTests       In-process HTTP tests: JSON shape, validation, 503, ETag/304, 429, health, warm-up
  BestStoriesHealthCheckTests    Healthy, degraded and unhealthy states
```

There is one project with feature folders. For one endpoint, separate Domain/Application/Infrastructure projects would add ceremony without making the code any safer. The Hacker News client sits behind `IHackerNewsClient` and time is read through `TimeProvider`, so tests run deterministically without a network.

Builds run with `TreatWarningsAsErrors` and the `latest-recommended` .NET analyzers ([`Directory.Build.props`](Directory.Build.props)).

## Assumptions

- **The candidates are the stories in `beststories.json`.** The API does not search all of Hacker News, so it can return at most the ~200 stories that endpoint lists.
- **"Best" is decided by score.** The API sorts by `score` and does not rely on the order of `beststories.json`. Stories with the same score are ordered newest first.
- **Data up to about a minute old is acceptable.** Scores and comment counts can be up to `CacheDuration` old, plus the time a refresh takes. In return, response times stay low and the load on Hacker News stays predictable.
- **If `n` is more than the number of stories available, all of them are returned.** This is not treated as an error.
- **Deleted, dead and missing items, and items that are not stories, are skipped.**
- **Text posts get a link to their discussion page.** Posts such as "Ask HN" have no `url`, so `uri` is set to `https://news.ycombinator.com/item?id={id}`. An empty `url` (`""`) is treated the same way.
- **An empty answer from Hacker News is a failure, not data.** If `beststories.json` returns `null` or `[]`, or no item comes back as a live story, the refresh counts as failed and the last good stories keep being served.
- **Titles are returned as Hacker News sends them.** The Hacker News documentation describes `title` as HTML, but the live data is plain text (e.g. `AT&T`, not `AT&amp;T`), so titles are not decoded. They are user content: consumers must escape them when rendering.
- **`commentCount` comes from the `descendants` field**, which counts all comments, including replies. If it is missing, `commentCount` is `0`.
- **`time` is UTC** and formatted as ISO 8601, as in the brief.
- **The API is public and read-only.** It has no authentication, and Swagger UI is enabled in every environment.
- **It runs as one or a few instances.** Each instance keeps its own snapshot (see below for running many).
- **The rate limit defaults are a guess.** The brief gives no traffic profile, so 100 requests per 10s per IP is a starting point to tune.

## What I would add next

- **A shared snapshot for many instances.** Each instance refreshes on its own, so N instances send N times the (small, fixed) traffic to Hacker News. I would have one background worker, or a refresh guarded by a distributed lock, write the snapshot to Redis, and have the API instances read it through `HybridCache`.
- **Fewer item requests per refresh.** I would use the [`updates.json`](https://github.com/HackerNews/API#changed-items-and-profiles) endpoint or Firebase streaming to refetch only the items that changed.
- **The real client IP behind a proxy.** I would configure `ForwardedHeaders` with the known proxy addresses so the rate limiter partitions by the real client.
- **OpenTelemetry export** of the existing metrics and traces to the platform's collector.
- **No very old data after idle periods.** Refreshes are triggered by requests, so after an hour without traffic the first request gets hour-old data while the refresh runs. I would either refresh on a timer, or make requests wait for the refresh when the data is older than `StaleAfter`.
- **Serialize once per snapshot.** Each request serializes its stories again. Caching the JSON bytes for each `n` (at most ~200 variants), for example with output caching, would remove most of the per-request work.
- **A contract test against the real API.** An opt-in test that calls Hacker News would catch changes to its schema, which the fake client cannot.
- **CI.** A pipeline that builds and runs the tests on every change.
- **Paging.** An `offset` parameter, if clients need more than the first page.

## AI usage

I used [Claude Code](https://claude.com/claude-code) while building this project:

- **Planning:** Claude Fable, to plan the design and the work.
- **Audit:** Claude Opus, to review the implementation.
