using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace HackerNews.Api.Stories;

/// <summary>An immutable, score-ordered view of the best stories as of <see cref="FetchedAt"/>.</summary>
public sealed class BestStoriesSnapshot
{
    private readonly Story[] _stories;

    public BestStoriesSnapshot(IEnumerable<KeyValuePair<int, Story>> storiesById, DateTimeOffset fetchedAt)
    {
        ById = storiesById.ToFrozenDictionary();
        _stories = ById.Values
            .OrderByDescending(story => story.Score)
            .ThenByDescending(story => story.Time)
            .ToArray();
        FetchedAt = fetchedAt;

        // Derived from the content rather than the fetch time, so every instance behind a load
        // balancer that loaded the same data hands out the same entity tag.
        Version = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(_stories)).AsSpan(0, 12));
    }

    /// <summary>All stories, highest score first. Stories with the same score are ordered newest first.</summary>
    public IReadOnlyList<Story> Stories => _stories;

    /// <summary>Stories keyed by their Hacker News id, used as a fallback when an item cannot be refetched.</summary>
    public FrozenDictionary<int, Story> ById { get; }

    public DateTimeOffset FetchedAt { get; }

    /// <summary>Opaque identifier of this snapshot, used as the HTTP entity tag.</summary>
    public string Version { get; }

    /// <summary>
    /// Returns the best <paramref name="count"/> stories without copying them. The view is read-only, so a
    /// caller cannot cast it to <see cref="IList{T}"/> and overwrite the snapshot every request shares.
    /// </summary>
    public IReadOnlyList<Story> Take(int count) =>
        new ReadOnlyCollection<Story>(new ArraySegment<Story>(_stories, 0, Math.Clamp(count, 0, _stories.Length)));
}
