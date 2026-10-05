namespace HackerNews.Api.HackerNews;

/// <summary>An item as returned by https://hacker-news.firebaseio.com/v0/item/{id}.json.</summary>
public sealed record HackerNewsItem
{
    public int Id { get; init; }
    public string? Type { get; init; }
    public string? By { get; init; }

    /// <summary>Creation time in Unix seconds.</summary>
    public long Time { get; init; }

    public string? Title { get; init; }
    public string? Url { get; init; }
    public int Score { get; init; }

    /// <summary>Total comment count for stories.</summary>
    public int? Descendants { get; init; }

    public bool Deleted { get; init; }
    public bool Dead { get; init; }
}
