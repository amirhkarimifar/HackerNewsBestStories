using System.ComponentModel.DataAnnotations;

namespace HackerNews.Api.HackerNews;

public sealed class HackerNewsOptions
{
    public const string SectionName = "HackerNews";

    [Required]
    public Uri BaseAddress { get; set; } = new("https://hacker-news.firebaseio.com/v0/");

    /// <summary>How long a fetched list of best stories is served before it is refreshed.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "1.00:00:00")]
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long to wait before trying again after a refresh fails.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan RetryAfterFailure { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Maximum number of item requests sent to Hacker News at the same time during a refresh.</summary>
    [Range(1, 64)]
    public int MaxConcurrentRequests { get; set; } = 8;

    /// <summary>
    /// Share of item requests in one refresh that may fail before the whole refresh is treated as failed.
    /// Failed items fall back to the version from the previous refresh, when there is one.
    /// </summary>
    [Range(0.0, 1.0)]
    public double MaxItemFailureRatio { get; set; } = 0.1;

    /// <summary>Load the stories in the background at startup so the first request does not wait for Hacker News.</summary>
    public bool WarmUpOnStartup { get; set; } = true;

    /// <summary>How long clients and shared caches (CDNs, proxies) may reuse a response.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan ClientCacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The health check reports <c>Degraded</c> when the stories served are older than this.</summary>
    [Range(typeof(TimeSpan), "00:00:10", "1.00:00:00")]
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(10);
}
