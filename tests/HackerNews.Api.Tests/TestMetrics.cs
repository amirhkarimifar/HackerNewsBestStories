using System.Diagnostics.Metrics;
using HackerNews.Api.Stories;
using Microsoft.Extensions.DependencyInjection;

namespace HackerNews.Api.Tests;

internal static class TestMetrics
{
    private static readonly IMeterFactory MeterFactory =
        new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();

    public static BestStoriesMetrics Create() => new(MeterFactory);
}
