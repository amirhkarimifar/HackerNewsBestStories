using HackerNews.Api.HackerNews;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.Stories;

/// <summary>
/// Readiness of the best stories: unhealthy until they are first loaded, degraded while the data being
/// served is older than <see cref="HackerNewsOptions.StaleAfter"/> (Hacker News keeps failing), healthy otherwise.
/// It only reads what is cached and never calls Hacker News, so probes add no load upstream.
/// </summary>
public sealed class BestStoriesHealthCheck(
    BestStoriesCache cache,
    IOptions<HackerNewsOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (cache.Current is not { } snapshot)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("The best stories have not been loaded from Hacker News yet."));
        }

        var age = timeProvider.GetUtcNow() - snapshot.FetchedAt;
        var data = new Dictionary<string, object>
        {
            ["stories"] = snapshot.Stories.Count,
            ["fetchedAt"] = snapshot.FetchedAt,
            ["ageSeconds"] = (int)age.TotalSeconds,
        };

        return Task.FromResult(age > options.Value.StaleAfter
            ? HealthCheckResult.Degraded($"The best stories were last refreshed {age.TotalMinutes:F0} minutes ago.", data: data)
            : HealthCheckResult.Healthy(data: data));
    }
}
