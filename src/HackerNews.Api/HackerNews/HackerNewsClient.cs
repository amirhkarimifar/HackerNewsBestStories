namespace HackerNews.Api.HackerNews;

public interface IHackerNewsClient
{
    Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken);

    /// <summary>Returns null when Hacker News has no item with this id.</summary>
    Task<HackerNewsItem?> GetItemAsync(int id, CancellationToken cancellationToken);
}

/// <summary>
/// Thin wrapper over the Hacker News Firebase API. It creates an <see cref="HttpClient"/> from the
/// factory per call so it can be a singleton without holding on to a handler (and its DNS) forever.
/// Timeouts, retries and the circuit breaker live in the HTTP pipeline (see <c>AddHackerNewsClient</c>).
/// </summary>
public sealed class HackerNewsClient(IHttpClientFactory httpClientFactory) : IHackerNewsClient
{
    public const string HttpClientName = "HackerNews";

    private static readonly Uri BestStoriesUri = new("beststories.json", UriKind.Relative);

    public async Task<IReadOnlyList<int>> GetBestStoryIdsAsync(CancellationToken cancellationToken) =>
        await CreateClient().GetFromJsonAsync<int[]>(BestStoriesUri, cancellationToken) ?? [];

    public Task<HackerNewsItem?> GetItemAsync(int id, CancellationToken cancellationToken) =>
        CreateClient().GetFromJsonAsync<HackerNewsItem>(new Uri($"item/{id}.json", UriKind.Relative), cancellationToken);

    private HttpClient CreateClient() => httpClientFactory.CreateClient(HttpClientName);
}
