using HackerNews.Api.HackerNews;
using HackerNews.Api.Stories;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HackerNews.Api.Infrastructure;

public static class ServiceCollectionExtensions
{
    public const string ReadinessTag = "ready";

    public static IServiceCollection AddBestStories(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(HackerNewsOptions.SectionName);
        services.AddOptions<HackerNewsOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Standard resilience pipeline: rate limiter, total timeout, retries with jittered backoff,
        // circuit breaker and per-attempt timeout. Tuned under HackerNews:Resilience in configuration;
        // without that section the library defaults apply (binding an empty section would throw).
        var httpClient = services.AddHttpClient(HackerNewsClient.HttpClientName, (serviceProvider, http) =>
        {
            http.BaseAddress = serviceProvider.GetRequiredService<IOptions<HackerNewsOptions>>().Value.BaseAddress;
            http.DefaultRequestHeaders.UserAgent.ParseAdd("HackerNewsBestStoriesApi/1.0");
        });
        var resilience = section.GetSection("Resilience");
        if (resilience.Exists())
        {
            httpClient.AddStandardResilienceHandler(resilience);
        }
        else
        {
            httpClient.AddStandardResilienceHandler();
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IHackerNewsClient, HackerNewsClient>();
        services.AddSingleton<BestStoriesMetrics>();
        services.AddSingleton<BestStoriesCache>();
        services.AddHostedService<BestStoriesWarmUp>();

        services.AddHealthChecks()
            .AddCheck<BestStoriesHealthCheck>("best-stories", tags: [ReadinessTag]);

        return services;
    }
}
