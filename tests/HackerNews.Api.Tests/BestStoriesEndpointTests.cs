using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HackerNews.Api.HackerNews;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HackerNews.Api.Tests;

public sealed class BestStoriesEndpointTests : IDisposable
{
    private readonly FakeHackerNewsClient _hackerNews = new();
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    [Fact]
    public async Task Returns_stories_in_the_documented_json_shape()
    {
        _hackerNews
            .AddStory(1, score: 5)
            .AddItem(new HackerNewsItem
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
        using var http = CreateClient();

        var response = await http.GetAsync(new Uri("/api/stories/best?n=1", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expected = """
            [{
              "title": "A uBlock Origin update was rejected from the Chrome Web Store",
              "uri": "https://github.com/uBlockOrigin/uBlock-issues/issues/745",
              "postedBy": "ismaildonmez",
              "time": "2019-10-12T13:43:01+00:00",
              "score": 1716,
              "commentCount": 572
            }]
            """;
        var body = await response.Content.ReadAsStringAsync();
        using var expectedJson = JsonDocument.Parse(expected);
        using var actualJson = JsonDocument.Parse(body);
        Assert.True(JsonElement.DeepEquals(expectedJson.RootElement, actualJson.RootElement), body);
    }

    [Fact]
    public async Task Returns_n_stories_sorted_by_score()
    {
        _hackerNews.AddStory(1, score: 10).AddStory(2, score: 30).AddStory(3, score: 20);
        using var http = CreateClient();

        using var json = JsonDocument.Parse(await http.GetStringAsync(new Uri("/api/stories/best?n=2", UriKind.Relative)));

        Assert.Equal([30, 20], json.RootElement.EnumerateArray().Select(s => s.GetProperty("score").GetInt32()));
    }

    [Theory]
    [InlineData("/api/stories/best")]
    [InlineData("/api/stories/best?n=0")]
    [InlineData("/api/stories/best?n=-3")]
    [InlineData("/api/stories/best?n=abc")]
    public async Task Rejects_a_missing_or_invalid_n(string url)
    {
        using var http = CreateClient();

        var response = await http.GetAsync(new Uri(url, UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _hackerNews.BestStoryIdsCalls);
    }

    [Fact]
    public async Task Returns_503_with_retry_after_when_hacker_news_is_unavailable_and_nothing_is_cached()
    {
        _hackerNews.Fail = true;
        using var http = CreateClient();

        var response = await http.GetAsync(new Uri("/api/stories/best?n=5", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TimeSpan.FromSeconds(15), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task Sets_caching_headers_and_answers_304_when_the_client_already_has_the_data()
    {
        _hackerNews.AddStory(1, score: 10);
        using var http = CreateClient();

        var first = await http.GetAsync(new Uri("/api/stories/best?n=1", UriKind.Relative));
        Assert.NotNull(first.Headers.ETag);
        Assert.True(first.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromSeconds(30), first.Headers.CacheControl?.MaxAge);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/stories/best?n=1");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag);
        var second = await http.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(first.Headers.ETag, second.Headers.ETag);
        Assert.Empty(await second.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Ignores_an_outdated_etag()
    {
        _hackerNews.AddStory(1, score: 10);
        using var http = CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/stories/best?n=1");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"outdated\"", isWeak: true));
        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Rate_limits_each_client()
    {
        _hackerNews.AddStory(1, score: 10);
        using var http = CreateClient(("RateLimiting:PermitLimit", "3"), ("RateLimiting:Window", "00:01:00"));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            statuses.Add((await http.GetAsync(new Uri("/api/stories/best?n=1", UriKind.Relative))).StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task Is_not_ready_until_the_stories_are_loaded()
    {
        _hackerNews.AddStory(1, score: 10);
        using var http = CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.GetAsync(new Uri("/health/ready", UriKind.Relative))).StatusCode);

        await http.GetAsync(new Uri("/api/stories/best?n=1", UriKind.Relative));

        var ready = await http.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var json = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Loads_the_stories_at_startup_when_warm_up_is_enabled()
    {
        _hackerNews.AddStory(1, score: 10);
        using var http = CreateClient(("HackerNews:WarmUpOnStartup", "true"));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((await http.GetAsync(new Uri("/health/ready", UriKind.Relative))).StatusCode != HttpStatusCode.OK)
        {
            Assert.True(DateTime.UtcNow < deadline, "The stories were not loaded at startup.");
            await Task.Delay(10);
        }

        Assert.Equal(1, _hackerNews.BestStoryIdsCalls);
    }

    [Fact]
    public async Task Publishes_the_openapi_document_and_swagger_ui()
    {
        using var http = CreateClient();

        using var document = JsonDocument.Parse(await http.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative)));
        var swagger = await http.GetAsync(new Uri("/swagger/index.html", UriKind.Relative));

        Assert.True(document.RootElement.GetProperty("paths").TryGetProperty("/api/stories/best", out _));
        Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
        Assert.Equal("text/html", swagger.Content.Headers.ContentType?.MediaType);
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    /// <summary>Starts the API against the fake Hacker News. Warm-up is off unless a setting turns it on.</summary>
    private HttpClient CreateClient(params (string Key, string Value)[] settings)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HackerNews:WarmUpOnStartup", "false");
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHackerNewsClient>();
                services.AddSingleton<IHackerNewsClient>(_hackerNews);
            });
        });
        _factories.Add(factory);
        return factory.CreateClient();
    }
}
