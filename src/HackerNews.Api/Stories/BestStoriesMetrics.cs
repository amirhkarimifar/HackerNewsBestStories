using System.Diagnostics.Metrics;

namespace HackerNews.Api.Stories;

/// <summary>
/// Metrics for the calls this API makes to Hacker News. Inspect them with <c>dotnet-counters monitor
/// -n HackerNews.Api --counters HackerNews.Api.BestStories</c> or export them with OpenTelemetry.
/// </summary>
public sealed class BestStoriesMetrics
{
    public const string MeterName = "HackerNews.Api.BestStories";

    private readonly Counter<long> _refreshes;
    private readonly Histogram<double> _refreshDuration;
    private readonly Counter<long> _itemFailures;

    public BestStoriesMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _refreshes = meter.CreateCounter<long>(
            "hackernews.refreshes", unit: "{refresh}", description: "Refreshes of the best stories from Hacker News.");
        _refreshDuration = meter.CreateHistogram<double>(
            "hackernews.refresh.duration", unit: "s", description: "Time taken to refresh the best stories.");
        _itemFailures = meter.CreateCounter<long>(
            "hackernews.item.failures", unit: "{item}", description: "Item requests to Hacker News that failed.");
    }

    public void RefreshCompleted(bool succeeded, TimeSpan duration)
    {
        var result = new KeyValuePair<string, object?>("result", succeeded ? "success" : "failure");
        _refreshes.Add(1, result);
        _refreshDuration.Record(duration.TotalSeconds, result);
    }

    public void ItemFailed() => _itemFailures.Add(1);
}
