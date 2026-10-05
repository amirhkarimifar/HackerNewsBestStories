using HackerNews.Api.HackerNews;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.Stories;

/// <summary>
/// Keeps the best stories, sorted by score, in memory and decides when to ask Hacker News again.
/// The load on Hacker News depends only on <see cref="HackerNewsOptions.CacheDuration"/>, not on
/// how many requests this API receives:
/// <list type="bullet">
///   <item>At most one refresh runs at a time. Concurrent callers share it.</item>
///   <item>Once there is data, callers never wait. Stale data is returned while a refresh runs in the background.</item>
///   <item>If a refresh fails, the last good data keeps being served and the next attempt waits
///         <see cref="HackerNewsOptions.RetryAfterFailure"/>.</item>
///   <item>Item requests within a refresh are limited to <see cref="HackerNewsOptions.MaxConcurrentRequests"/>.
///         An item that fails keeps its version from the previous refresh, so one flaky request does
///         not throw away the other ~200; too many failures fail the refresh as a whole.</item>
/// </list>
/// </summary>
public sealed class BestStoriesCache(
    IHackerNewsClient client,
    IOptions<HackerNewsOptions> options,
    BestStoriesMetrics metrics,
    TimeProvider timeProvider,
    ILogger<BestStoriesCache> logger) : IDisposable
{
    private readonly Lock _gate = new();

    // Cancelled when the host shuts down (the container disposes this singleton), so an in-flight
    // refresh does not keep calling Hacker News after the app has been asked to stop.
    private readonly CancellationTokenSource _shutdown = new();
    private BestStoriesSnapshot? _snapshot;
    private Task<BestStoriesSnapshot>? _refresh;
    private DateTimeOffset _refreshDueAt = DateTimeOffset.MinValue;

    /// <summary>The latest stories loaded, or null if nothing has been loaded yet. Never triggers a refresh.</summary>
    public BestStoriesSnapshot? Current => Volatile.Read(ref _snapshot);

    public async Task<IReadOnlyList<Story>> GetBestStoriesAsync(int count, CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.Take(count);
    }

    public Task<BestStoriesSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        Task<BestStoriesSnapshot> refresh;
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            var refreshInFlight = _refresh is { IsCompleted: false };
            if (!refreshInFlight && now >= _refreshDueAt && !_shutdown.IsCancellationRequested)
            {
                _refresh = StartRefresh(startedAt: now);
            }

            if (_snapshot is not null)
            {
                return Task.FromResult(_snapshot);
            }

            // No data yet: wait for the refresh in flight, or rethrow the last failure while backing off.
            // (The only way there is none is a shutdown before the first request.)
            refresh = _refresh ?? throw new ObjectDisposedException(nameof(BestStoriesCache));
        }

        return refresh.WaitAsync(cancellationToken);
    }

    // Cancel only: the source owns no timer or wait handle, and leaving it undisposed means a request
    // racing with shutdown can still read its token instead of hitting ObjectDisposedException.
    public void Dispose() => _shutdown.Cancel();

    private Task<BestStoriesSnapshot> StartRefresh(DateTimeOffset startedAt)
    {
        var stopping = _shutdown.Token;
        var refresh = Task.Run(() => RefreshAsync(startedAt, stopping), CancellationToken.None);

        // A failed refresh is rethrown to every caller still waiting, but those callers may all have
        // given up (request aborted). Observe the exception so it never surfaces as unobserved.
        refresh.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return refresh;
    }

    private async Task<BestStoriesSnapshot> RefreshAsync(DateTimeOffset startedAt, CancellationToken stopping)
    {
        var started = timeProvider.GetTimestamp();
        try
        {
            var snapshot = await FetchAsync(Current, stopping);
            lock (_gate)
            {
                Volatile.Write(ref _snapshot, snapshot);
                _refreshDueAt = startedAt + options.Value.CacheDuration;
            }

            metrics.RefreshCompleted(succeeded: true, timeProvider.GetElapsedTime(started));
            Log.Refreshed(logger, snapshot.Stories.Count);
            return snapshot;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics.RefreshCompleted(succeeded: false, timeProvider.GetElapsedTime(started));
            BestStoriesSnapshot? stale;
            lock (_gate)
            {
                stale = _snapshot;
                _refreshDueAt = startedAt + options.Value.RetryAfterFailure;
            }

            if (stale is not null)
            {
                Log.RefreshFailedServingStale(logger, ex, stale.FetchedAt);
                return stale;
            }

            Log.RefreshFailedNoData(logger, ex);
            throw new HackerNewsUnavailableException("Could not load the best stories from Hacker News.", ex);
        }
    }

    private async Task<BestStoriesSnapshot> FetchAsync(BestStoriesSnapshot? previous, CancellationToken stopping)
    {
        var ids = (await client.GetBestStoryIdsAsync(stopping)).Distinct().ToArray();

        // Firebase answers `null` (or an empty list) during hiccups. Treat that as a failed refresh, so the
        // last good stories keep being served instead of being replaced by an empty list.
        if (ids.Length == 0)
        {
            throw new HackerNewsUnavailableException("Hacker News returned no best story ids.");
        }

        // Indexed by position in `ids`, so parallel workers never write to the same slot.
        var stories = new Story?[ids.Length];
        var failures = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.Value.MaxConcurrentRequests,
            CancellationToken = stopping,
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, ids.Length), parallelOptions, async (index, ct) =>
        {
            var id = ids[index];
            try
            {
                var item = await client.GetItemAsync(id, ct);
                stories[index] = item is { Deleted: false, Dead: false, Type: "story" } ? Story.FromItem(item) : null;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref failures);
                metrics.ItemFailed();
                Log.ItemFailed(logger, ex, id);
                stories[index] = previous?.ById.GetValueOrDefault(id);
            }
        });

        if (failures > 0)
        {
            var allowed = (int)Math.Floor(ids.Length * options.Value.MaxItemFailureRatio);
            if (failures > allowed)
            {
                throw new HackerNewsUnavailableException(
                    $"{failures} of {ids.Length} item requests to Hacker News failed (at most {allowed} allowed).");
            }

            Log.SomeItemsFailed(logger, failures, ids.Length);
        }

        var loaded = ids
            .Select((id, index) => (id, story: stories[index]))
            .Where(entry => entry.story is not null)
            .Select(entry => KeyValuePair.Create(entry.id, entry.story!))
            .ToList();
        if (loaded.Count == 0)
        {
            throw new HackerNewsUnavailableException($"None of the {ids.Length} best story ids returned a live story.");
        }

        return new BestStoriesSnapshot(loaded, timeProvider.GetUtcNow());
    }
}
