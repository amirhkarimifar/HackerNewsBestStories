using HackerNews.Api.HackerNews;
using HackerNews.Api.Stories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace HackerNews.Api.Tests;

public class BestStoriesCacheTests
{
    private static readonly HackerNewsOptions DefaultOptions = new()
    {
        CacheDuration = TimeSpan.FromMinutes(1),
        RetryAfterFailure = TimeSpan.FromSeconds(15),
        MaxConcurrentRequests = 8,
    };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeHackerNewsClient _hackerNews = new();

    private BestStoriesCache CreateCache(HackerNewsOptions? options = null) =>
        new(_hackerNews, Options.Create(options ?? DefaultOptions), TestMetrics.Create(), _time, NullLogger<BestStoriesCache>.Instance);

    [Fact]
    public async Task Returns_the_top_n_stories_in_descending_order_of_score()
    {
        _hackerNews.AddStory(1, score: 10).AddStory(2, score: 300).AddStory(3, score: 50).AddStory(4, score: 200);
        var cache = CreateCache();

        var stories = await GetBestAsync(cache, 3);

        Assert.Equal([300, 200, 50], stories.Select(s => s.Score));
    }

    [Fact]
    public async Task Returns_every_story_when_n_exceeds_the_number_available()
    {
        _hackerNews.AddStory(1, score: 10).AddStory(2, score: 20);
        var cache = CreateCache();

        var stories = await GetBestAsync(cache, 500);

        Assert.Equal(2, stories.Count);
    }

    [Fact]
    public async Task Maps_hacker_news_items_to_the_response_shape()
    {
        _hackerNews.AddItem(new HackerNewsItem
        {
            Id = 21233041,
            Type = "story",
            By = "ismaildonmez",
            Time = 1570887781,
            Title = "A uBlock Origin update was rejected from the Chrome Web Store",
            Url = "https://github.com/uBlockOrigin/uBlock-issues/issues/745",
            Score = 1716,
            Descendants = 572,
        }, 21233041);
        var cache = CreateCache();

        var story = Assert.Single(await GetBestAsync(cache, 1));

        Assert.Equal(
            new Story(
                "A uBlock Origin update was rejected from the Chrome Web Store",
                "https://github.com/uBlockOrigin/uBlock-issues/issues/745",
                "ismaildonmez",
                new DateTimeOffset(2019, 10, 12, 13, 43, 1, TimeSpan.Zero),
                1716,
                572),
            story);
    }

    [Fact]
    public async Task Links_text_posts_without_a_url_to_their_discussion_page()
    {
        _hackerNews.AddItem(new HackerNewsItem { Id = 42, Type = "story", Title = "Ask HN: Anything?", Score = 5 }, 42);
        var cache = CreateCache();

        var story = Assert.Single(await GetBestAsync(cache, 1));

        Assert.Equal("https://news.ycombinator.com/item?id=42", story.Uri);
        Assert.Equal(0, story.CommentCount);
    }

    [Fact]
    public async Task Skips_missing_deleted_dead_and_non_story_items()
    {
        _hackerNews
            .AddStory(1, score: 10)
            .AddItem(null, 2)
            .AddItem(new HackerNewsItem { Id = 3, Type = "story", Score = 99, Deleted = true }, 3)
            .AddItem(new HackerNewsItem { Id = 4, Type = "story", Score = 99, Dead = true }, 4)
            .AddItem(new HackerNewsItem { Id = 5, Type = "job", Score = 99 }, 5);
        var cache = CreateCache();

        var stories = await GetBestAsync(cache, 10);

        Assert.Equal(["Story 1"], stories.Select(s => s.Title));
    }

    [Fact]
    public async Task Concurrent_callers_share_a_single_fetch_from_hacker_news()
    {
        for (var id = 1; id <= 20; id++)
        {
            _hackerNews.AddStory(id, score: id);
        }

        var gate = new TaskCompletionSource();
        _hackerNews.BestStoryIdsGate = gate.Task;
        var cache = CreateCache();

        var callers = Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => GetBestAsync(cache, 5)))
            .ToArray();
        await Eventually(() => _hackerNews.BestStoryIdsCalls == 1);
        gate.SetResult();
        var results = await Task.WhenAll(callers);

        Assert.All(results, stories => Assert.Equal([20, 19, 18, 17, 16], stories.Select(s => s.Score)));
        Assert.Equal(1, _hackerNews.BestStoryIdsCalls);
        Assert.Equal(20, _hackerNews.ItemCalls);
    }

    [Fact]
    public async Task Serves_from_memory_until_the_cache_duration_has_passed()
    {
        _hackerNews.AddStory(1, score: 10);
        var cache = CreateCache();

        await GetBestAsync(cache, 1);
        _time.Advance(TimeSpan.FromSeconds(59));
        await GetBestAsync(cache, 1);
        await GetBestAsync(cache, 1);

        Assert.Equal(1, _hackerNews.BestStoryIdsCalls);
    }

    [Fact]
    public async Task Serves_stale_stories_immediately_while_refreshing_in_the_background()
    {
        _hackerNews.AddStory(1, score: 10, title: "Old");
        var cache = CreateCache();
        await GetBestAsync(cache, 1);

        var gate = new TaskCompletionSource();
        _hackerNews.BestStoryIdsGate = gate.Task;
        _hackerNews.AddStory(2, score: 20, title: "New");
        _time.Advance(TimeSpan.FromMinutes(1));

        var duringRefresh = await GetBestAsync(cache, 1);
        Assert.Equal("Old", Assert.Single(duringRefresh).Title);

        gate.SetResult();
        await Eventually(async () => (await GetBestAsync(cache, 1))[0].Title == "New");
        Assert.Equal(2, _hackerNews.BestStoryIdsCalls);
    }

    [Fact]
    public async Task Keeps_serving_previous_stories_when_hacker_news_fails_and_backs_off()
    {
        _hackerNews.AddStory(1, score: 10);
        var cache = CreateCache();
        await GetBestAsync(cache, 1);

        _hackerNews.Fail = true;
        _time.Advance(TimeSpan.FromMinutes(1));

        var stories = await GetBestAsync(cache, 1);
        Assert.Equal("Story 1", Assert.Single(stories).Title);
        await Eventually(() => _hackerNews.BestStoryIdsCalls == 2);

        // Within the back-off window nobody calls Hacker News again.
        _time.Advance(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 10; i++)
        {
            await GetBestAsync(cache, 1);
        }

        Assert.Equal(2, _hackerNews.BestStoryIdsCalls);

        // Once the back-off has passed (and the failed refresh has finished), the next request retries.
        _time.Advance(TimeSpan.FromSeconds(5));
        await Eventually(async () =>
        {
            await GetBestAsync(cache, 1);
            return _hackerNews.BestStoryIdsCalls == 3;
        });
    }

    [Fact]
    public async Task Throws_unavailable_when_hacker_news_fails_before_any_stories_were_loaded()
    {
        _hackerNews.AddStory(1, score: 10);
        _hackerNews.Fail = true;
        var cache = CreateCache();

        await Assert.ThrowsAsync<HackerNewsUnavailableException>(() => GetBestAsync(cache, 1));
        await Assert.ThrowsAsync<HackerNewsUnavailableException>(() => GetBestAsync(cache, 1));
        Assert.Equal(1, _hackerNews.BestStoryIdsCalls);

        _hackerNews.Fail = false;
        _time.Advance(TimeSpan.FromSeconds(15));

        var stories = await GetBestAsync(cache, 1);
        Assert.Single(stories);
        Assert.Equal(2, _hackerNews.BestStoryIdsCalls);
    }

    [Fact]
    public async Task Limits_the_number_of_item_requests_in_flight()
    {
        for (var id = 1; id <= 40; id++)
        {
            _hackerNews.AddStory(id, score: id);
        }

        _hackerNews.ItemDelay = TimeSpan.FromMilliseconds(20);
        var cache = CreateCache(new HackerNewsOptions { MaxConcurrentRequests = 4 });

        await GetBestAsync(cache, 1);

        Assert.Equal(40, _hackerNews.ItemCalls);
        Assert.InRange(_hackerNews.MaxItemCallsInFlight, 2, 4);
    }

    [Fact]
    public async Task Keeps_the_previous_version_of_a_story_whose_refetch_fails()
    {
        _hackerNews.AddStory(1, score: 10, title: "One").AddStory(2, score: 20, title: "Two (old)");
        for (var id = 3; id <= 20; id++)
        {
            _hackerNews.AddStory(id, score: 1);
        }

        var cache = CreateCache();
        await GetBestAsync(cache, 1);

        _hackerNews.AddStory(1, score: 500, title: "One (new)").AddStory(2, score: 30, title: "Two (new)");
        _hackerNews.FailingItemIds.Add(2);
        _time.Advance(TimeSpan.FromMinutes(1));
        await GetBestAsync(cache, 1);
        await Eventually(async () => (await GetBestAsync(cache, 1))[0].Title == "One (new)");

        var stories = await GetBestAsync(cache, 2);
        Assert.Equal(["One (new)", "Two (old)"], stories.Select(s => s.Title));
    }

    [Fact]
    public async Task Fails_the_refresh_when_too_many_item_requests_fail_and_keeps_serving_the_previous_stories()
    {
        _hackerNews.AddStory(1, score: 10, title: "Old").AddStory(2, score: 5);
        var cache = CreateCache();
        await GetBestAsync(cache, 1);

        _hackerNews.AddStory(3, score: 99, title: "New");
        _hackerNews.FailingItemIds.UnionWith([1, 2]);
        _time.Advance(TimeSpan.FromMinutes(1));
        await GetBestAsync(cache, 1);
        await Eventually(() => _hackerNews.BestStoryIdsCalls == 2);

        // A failed refresh backs off for RetryAfterFailure (15s), not CacheDuration (60s).
        _time.Advance(TimeSpan.FromSeconds(15));
        await Eventually(async () =>
        {
            await GetBestAsync(cache, 1);
            return _hackerNews.BestStoryIdsCalls == 3;
        });

        Assert.Equal("Old", Assert.Single(await GetBestAsync(cache, 1)).Title);
    }

    [Fact]
    public async Task Gives_identical_content_the_same_version_and_changed_content_a_new_one()
    {
        _hackerNews.AddStory(1, score: 10);
        var cache = CreateCache();
        var first = await cache.GetSnapshotAsync(CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(1));
        await cache.GetSnapshotAsync(CancellationToken.None);
        await Eventually(() => cache.Current != first);
        var unchanged = cache.Current!;

        _hackerNews.AddStory(1, score: 11);
        _time.Advance(TimeSpan.FromMinutes(1));
        await cache.GetSnapshotAsync(CancellationToken.None);
        await Eventually(() => cache.Current != unchanged);

        Assert.Equal(first.Version, unchanged.Version);
        Assert.NotEqual(first.Version, cache.Current!.Version);
    }

    [Fact]
    public async Task Stops_refreshing_once_disposed()
    {
        _hackerNews.AddStory(1, score: 10);
        _hackerNews.BestStoryIdsGate = new TaskCompletionSource().Task;
        var cache = CreateCache();

        var waiting = cache.GetSnapshotAsync(CancellationToken.None);
        await Eventually(() => _hackerNews.BestStoryIdsCalls == 1);
        cache.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task Keeps_serving_previous_stories_when_hacker_news_returns_no_ids()
    {
        _hackerNews.AddStory(1, score: 10);
        var cache = CreateCache();
        await GetBestAsync(cache, 1);

        _hackerNews.BestStoryIds.Clear();
        _time.Advance(TimeSpan.FromMinutes(1));
        await GetBestAsync(cache, 1);

        await AssertLastRefreshFailed(cache);
        Assert.Equal("Story 1", Assert.Single(await GetBestAsync(cache, 1)).Title);
    }

    [Fact]
    public async Task Keeps_serving_previous_stories_when_no_item_comes_back_as_a_live_story()
    {
        _hackerNews.AddStory(1, score: 10);
        var cache = CreateCache();
        await GetBestAsync(cache, 1);

        _hackerNews.AddItem(null, 1);
        _time.Advance(TimeSpan.FromMinutes(1));
        await GetBestAsync(cache, 1);

        await AssertLastRefreshFailed(cache);
        Assert.Equal("Story 1", Assert.Single(await GetBestAsync(cache, 1)).Title);
    }

    [Fact]
    public async Task Throws_unavailable_when_the_first_refresh_returns_no_ids()
    {
        var cache = CreateCache();

        await Assert.ThrowsAsync<HackerNewsUnavailableException>(() => GetBestAsync(cache, 1));
    }

    [Fact]
    public async Task Links_stories_with_an_empty_url_to_their_discussion_page()
    {
        _hackerNews.AddItem(new HackerNewsItem { Id = 7, Type = "story", Title = "Empty url", Url = "", Score = 5 }, 7);
        var cache = CreateCache();

        var story = Assert.Single(await GetBestAsync(cache, 1));

        Assert.Equal("https://news.ycombinator.com/item?id=7", story.Uri);
    }

    [Fact]
    public async Task Returned_stories_cannot_be_modified_by_the_caller()
    {
        _hackerNews.AddStory(1, score: 10, title: "First").AddStory(2, score: 5, title: "Second");
        var cache = CreateCache();

        var stories = await GetBestAsync(cache, 2);

        Assert.Throws<NotSupportedException>(() => ((IList<Story>)stories)[0] = stories[1]);
        Assert.Equal("First", (await GetBestAsync(cache, 1))[0].Title);
    }

    /// <summary>
    /// Proves the latest refresh failed: a failed refresh is retried after RetryAfterFailure (15s),
    /// whereas a successful one would not be repeated before CacheDuration (60s).
    /// </summary>
    private async Task AssertLastRefreshFailed(BestStoriesCache cache)
    {
        await Eventually(() => _hackerNews.BestStoryIdsCalls == 2);
        _time.Advance(TimeSpan.FromSeconds(15));
        await Eventually(async () =>
        {
            await GetBestAsync(cache, 1);
            return _hackerNews.BestStoryIdsCalls == 3;
        });
    }

    /// <summary>Reads the best stories the way the endpoint does: the current snapshot, cut to <paramref name="count"/>.</summary>
    private static async Task<IReadOnlyList<Story>> GetBestAsync(BestStoriesCache cache, int count) =>
        (await cache.GetSnapshotAsync(CancellationToken.None)).Take(count);

    private static Task Eventually(Func<bool> condition) => Eventually(() => Task.FromResult(condition()));

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Condition was not met within 5 seconds.");
            }

            await Task.Delay(10);
        }
    }
}
