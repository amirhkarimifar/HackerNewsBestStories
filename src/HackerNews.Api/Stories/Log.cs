namespace HackerNews.Api.Stories;

/// <summary>Source-generated, allocation-free log messages for the best stories feature.</summary>
internal static partial class Log
{
    [LoggerMessage(1, LogLevel.Information, "Refreshed {Count} best stories from Hacker News")]
    public static partial void Refreshed(ILogger logger, int count);

    [LoggerMessage(2, LogLevel.Warning, "Refreshing best stories failed; serving the data loaded at {FetchedAt}")]
    public static partial void RefreshFailedServingStale(ILogger logger, Exception exception, DateTimeOffset fetchedAt);

    [LoggerMessage(3, LogLevel.Error, "Refreshing best stories failed and there is no previous data to serve")]
    public static partial void RefreshFailedNoData(ILogger logger, Exception exception);

    [LoggerMessage(4, LogLevel.Debug, "Fetching item {ItemId} from Hacker News failed")]
    public static partial void ItemFailed(ILogger logger, Exception exception, int itemId);

    [LoggerMessage(5, LogLevel.Warning, "{Failures} of {Total} item requests to Hacker News failed; their previous versions are served where available")]
    public static partial void SomeItemsFailed(ILogger logger, int failures, int total);

    [LoggerMessage(6, LogLevel.Information, "Warm-up loaded {Count} best stories")]
    public static partial void WarmedUp(ILogger logger, int count);
}
