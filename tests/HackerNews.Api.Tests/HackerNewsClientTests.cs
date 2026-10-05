using System.Net;
using System.Text;
using HackerNews.Api.HackerNews;
using HackerNews.Api.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HackerNews.Api.Tests;

/// <summary>
/// Tests the real <see cref="HackerNewsClient"/> as the app registers it (base address, User-Agent,
/// resilience pipeline), with only the network replaced by canned Hacker News responses.
/// </summary>
public sealed class HackerNewsClientTests : IDisposable
{
    private readonly StubHandler _handler = new();
    private readonly ServiceProvider _services;
    private readonly IHackerNewsClient _client;

    public HackerNewsClientTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddBestStories(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HackerNews:Resilience:Retry:MaxRetryAttempts"] = "2" })
            .Build());
        services.AddHttpClient(HackerNewsClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _handler);

        _services = services.BuildServiceProvider();
        _client = _services.GetRequiredService<IHackerNewsClient>();
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task Reads_the_best_story_ids_from_the_hacker_news_api()
    {
        _handler.Responses["/v0/beststories.json"] = "[21233041,21231208,21228524]";

        var ids = await _client.GetBestStoryIdsAsync(CancellationToken.None);

        Assert.Equal([21233041, 21231208, 21228524], ids);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(new Uri("https://hacker-news.firebaseio.com/v0/beststories.json"), request.RequestUri);
        Assert.Contains("HackerNewsBestStoriesApi", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Returns_no_ids_when_hacker_news_answers_null()
    {
        _handler.Responses["/v0/beststories.json"] = "null";

        Assert.Empty(await _client.GetBestStoryIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reads_a_story_as_hacker_news_sends_it()
    {
        // Captured from https://hacker-news.firebaseio.com/v0/item/21233041.json (kids shortened).
        _handler.Responses["/v0/item/21233041.json"] = """
            {"by":"ismaildonmez","descendants":572,"id":21233041,"kids":[21233229,21233577],"score":1716,
             "time":1570887781,"title":"A uBlock Origin update was rejected from the Chrome Web Store",
             "type":"story","url":"https://github.com/uBlockOrigin/uBlock-issues/issues/745"}
            """;

        var item = await _client.GetItemAsync(21233041, CancellationToken.None);

        Assert.Equal(
            new HackerNewsItem
            {
                Id = 21233041,
                Type = "story",
                By = "ismaildonmez",
                Time = 1570887781,
                Title = "A uBlock Origin update was rejected from the Chrome Web Store",
                Url = "https://github.com/uBlockOrigin/uBlock-issues/issues/745",
                Score = 1716,
                Descendants = 572,
            },
            item);
    }

    [Fact]
    public async Task Reads_a_deleted_item_which_has_almost_no_fields()
    {
        _handler.Responses["/v0/item/42.json"] = """{"deleted":true,"id":42,"time":1570887781,"type":"story"}""";

        var item = await _client.GetItemAsync(42, CancellationToken.None);

        Assert.NotNull(item);
        Assert.True(item.Deleted);
        Assert.Null(item.Title);
        Assert.Null(item.Descendants);
    }

    [Fact]
    public async Task Returns_null_for_an_item_hacker_news_does_not_have()
    {
        _handler.Responses["/v0/item/99.json"] = "null";

        Assert.Null(await _client.GetItemAsync(99, CancellationToken.None));
    }

    [Fact]
    public async Task Fails_when_hacker_news_returns_an_error()
    {
        // No canned response: the stub answers 404, which is not retried and surfaces as an exception.
        await Assert.ThrowsAsync<HttpRequestException>(() => _client.GetItemAsync(7, CancellationToken.None));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Responses { get; } = [];

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = Responses.TryGetValue(request.RequestUri!.AbsolutePath, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }
    }
}
