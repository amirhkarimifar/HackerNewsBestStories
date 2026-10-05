using HackerNews.Api.HackerNews;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.Stories;

/// <summary>
/// Starts the first refresh as soon as the app starts, so the first request does not pay for it.
/// It does not block startup: requests that arrive before it finishes share the same refresh.
/// </summary>
public sealed class BestStoriesWarmUp(
    BestStoriesCache cache,
    IOptions<HackerNewsOptions> options,
    ILogger<BestStoriesWarmUp> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WarmUpOnStartup)
        {
            return;
        }

        try
        {
            var snapshot = await cache.GetSnapshotAsync(stoppingToken);
            Log.WarmedUp(logger, snapshot.Stories.Count);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (HackerNewsUnavailableException)
        {
            // The cache has already logged the failure; the next request retries after the back-off.
        }
    }
}
