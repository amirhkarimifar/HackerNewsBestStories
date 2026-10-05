using HackerNews.Api.HackerNews;
using HackerNews.Api.Stories;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HackerNews.Api.Tests;

public sealed class BestStoriesHealthCheckTests : IDisposable
{
    private static readonly IOptions<HackerNewsOptions> Options = Microsoft.Extensions.Options.Options.Create(new HackerNewsOptions
    {
        CacheDuration = TimeSpan.FromMinutes(1),
        StaleAfter = TimeSpan.FromMinutes(10),
    });

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeHackerNewsClient _hackerNews = new();
    private readonly BestStoriesCache _cache;
    private readonly BestStoriesHealthCheck _healthCheck;

    public BestStoriesHealthCheckTests()
    {
        _cache = new BestStoriesCache(_hackerNews, Options, TestMetrics.Create(), _time, NullLogger<BestStoriesCache>.Instance);
        _healthCheck = new BestStoriesHealthCheck(_cache, Options, _time);
    }

    [Fact]
    public async Task Is_unhealthy_before_anything_is_loaded_and_never_calls_hacker_news()
    {
        var result = await _healthCheck.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(0, _hackerNews.BestStoryIdsCalls);
    }

    [Fact]
    public async Task Is_healthy_with_fresh_data_and_degraded_once_it_is_stale()
    {
        _hackerNews.AddStory(1, score: 10);
        await _cache.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, (await _healthCheck.CheckHealthAsync(new HealthCheckContext())).Status);

        _time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(HealthStatus.Degraded, (await _healthCheck.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    public void Dispose() => _cache.Dispose();
}
