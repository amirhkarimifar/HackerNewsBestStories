using HackerNews.Api.HackerNews;

namespace HackerNews.Api.Stories;

public sealed record Story(
    string Title,
    string Uri,
    string PostedBy,
    DateTimeOffset Time,
    int Score,
    int CommentCount)
{
    public static Story FromItem(HackerNewsItem item) => new(
        Title: item.Title ?? string.Empty,
        // Text posts (Ask HN etc.) have no url, and Hacker News sometimes sends "" instead of omitting it;
        // link to the discussion page in both cases.
        Uri: string.IsNullOrWhiteSpace(item.Url) ? $"https://news.ycombinator.com/item?id={item.Id}" : item.Url,
        PostedBy: item.By ?? string.Empty,
        Time: DateTimeOffset.FromUnixTimeSeconds(item.Time),
        Score: item.Score,
        CommentCount: item.Descendants ?? 0);
}
