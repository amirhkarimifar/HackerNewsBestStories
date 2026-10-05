using System.Collections.Concurrent;
using HackerNews.Api.HackerNews;

namespace HackerNews.Api.Tests;

/// <summary>In-memory stand-in for Hacker News that counts calls and can be slowed down or made to fail.</summary>
public sealed class FakeHackerNewsClient : IHackerNewsClient
{
    private readonly ConcurrentDictionary<int, HackerNewsItem?> _items = new();
    private int _bestStoryIdsCalls;
    private int _itemCalls;
    private int _itemCallsInFlight;
    private int _maxItemCallsInFlight;

    public List<int> BestStoryIds { get; } = [];

    /// <summary>When set, <see cref="GetBestStoryIdsAsync"/> waits for this task before answering.</summary>
    public Task? BestStoryIdsGate { get; set; }

    /// <summary>When true, <see cref="GetBestStoryIdsAsync"/> throws as if Hacker News were down.</summary>
    public bool Fail { get; set; }

    public TimeSpan ItemDelay { get; set; }

    /// <summary>Item ids whose requests throw as if that single call to Hacker News failed.</summary>
    public HashSet<int> FailingItemIds { get; } = [];

    public int BestStoryIdsCalls => Volatile.Read(ref _bestStoryIdsCalls);
    public int ItemCalls => Volatile.Read(ref _itemCalls);
    public int MaxItemCallsInFlight => Volatile.Read(ref _maxItemCallsInFlight);

    public FakeHackerNewsClient AddStory(int id, int score, string? title = null, long time = 1_570_000_000)
    {
        if (!BestStoryIds.Contains(id))
        {
            BestStoryIds.Add(id);
        }

        _items[id] = new HackerNewsItem
        {
            Id = id,
            Type = "story",
            By = $"user{id}",
            Time = time,
            Title = title ?? $"Story {id}",
            Url = $"https://example.com/{id}",
            Score = score,
            Descendants = id % 7,
        };
        return this;
    }

    public FakeHackerNewsClient AddItem(HackerNewsItem? item, int id)
    {
        BestStoryIds.Add(id);
        _items[id] = item;
        return this;
    }

    public async Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _bestStoryIdsCalls);
        if (BestStoryIdsGate is not null)
        {
            await BestStoryIdsGate.WaitAsync(cancellationToken);
        }

        if (Fail)
        {
            throw new HttpRequestException("Hacker News is down");
        }

        return BestStoryIds.ToArray();
    }

    public async Task<HackerNewsItem?> GetItemAsync(int id, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _itemCalls);
        var inFlight = Interlocked.Increment(ref _itemCallsInFlight);
        UpdateMax(inFlight);
        try
        {
            if (ItemDelay > TimeSpan.Zero)
            {
                await Task.Delay(ItemDelay, cancellationToken);
            }

            if (FailingItemIds.Contains(id))
            {
                throw new HttpRequestException($"Item {id} timed out");
            }

            return _items.GetValueOrDefault(id);
        }
        finally
        {
            Interlocked.Decrement(ref _itemCallsInFlight);
        }
    }

    private void UpdateMax(int inFlight)
    {
        var max = Volatile.Read(ref _maxItemCallsInFlight);
        while (inFlight > max)
        {
            var previous = Interlocked.CompareExchange(ref _maxItemCallsInFlight, inFlight, max);
            if (previous == max)
            {
                return;
            }

            max = previous;
        }
    }
}
