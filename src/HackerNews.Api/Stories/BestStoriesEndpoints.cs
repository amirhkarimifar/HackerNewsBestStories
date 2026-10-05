using System.ComponentModel;
using System.Globalization;
using HackerNews.Api.HackerNews;
using HackerNews.Api.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace HackerNews.Api.Stories;

public static class BestStoriesEndpoints
{
    public static RouteHandlerBuilder MapBestStories(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/stories/best", GetBestStoriesAsync)
            .WithName("GetBestStories")
            .WithTags("Stories")
            .WithSummary("Returns the best n Hacker News stories in descending order of score.")
            .WithDescription(
                "Served from an in-memory snapshot that is refreshed from Hacker News at most once per cache period. " +
                "Responses carry an ETag; send it back in If-None-Match to get 304 Not Modified while the data is unchanged.")
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .RequireRateLimiting(RateLimiting.PerClientPolicy);

    internal static async Task<Results<Ok<IReadOnlyList<Story>>, StatusCodeHttpResult, ProblemHttpResult>> GetBestStoriesAsync(
        [Description("Number of stories to return; at least 1. All stories are returned if fewer are available.")] int? n,
        HttpContext httpContext,
        BestStoriesCache cache,
        IOptions<HackerNewsOptions> options,
        CancellationToken cancellationToken)
    {
        if (n is null or < 1)
        {
            return TypedResults.Problem(
                title: "Invalid number of stories",
                detail: "Query parameter 'n' is required and must be a positive integer, e.g. /api/stories/best?n=10.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        BestStoriesSnapshot snapshot;
        try
        {
            snapshot = await cache.GetSnapshotAsync(cancellationToken);
        }
        catch (HackerNewsUnavailableException)
        {
            httpContext.Response.Headers.RetryAfter = ToSeconds(options.Value.RetryAfterFailure);
            return TypedResults.Problem(
                title: "Hacker News is unavailable",
                detail: "The best stories could not be loaded from Hacker News. Please try again later.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // The URL (including n) is the cache key for clients and CDNs, so the snapshot version is
        // enough to identify the representation.
        var etag = new EntityTagHeaderValue($"\"{snapshot.Version}\"", isWeak: true);
        var responseHeaders = httpContext.Response.GetTypedHeaders();
        responseHeaders.ETag = etag;
        responseHeaders.LastModified = snapshot.FetchedAt;
        responseHeaders.CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = options.Value.ClientCacheDuration };

        var ifNoneMatch = httpContext.Request.GetTypedHeaders().IfNoneMatch;
        if (ifNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(etag, useStrongComparison: false)))
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        return TypedResults.Ok(snapshot.Take(n.Value));
    }

    private static string ToSeconds(TimeSpan duration) =>
        ((int)Math.Ceiling(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
}
