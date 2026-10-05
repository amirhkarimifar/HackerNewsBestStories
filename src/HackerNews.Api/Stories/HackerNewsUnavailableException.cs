namespace HackerNews.Api.Stories;

/// <summary>Thrown when the best stories cannot be loaded from Hacker News and nothing is cached to fall back on.</summary>
public sealed class HackerNewsUnavailableException : Exception
{
    public HackerNewsUnavailableException()
    {
    }

    public HackerNewsUnavailableException(string message)
        : base(message)
    {
    }

    public HackerNewsUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
