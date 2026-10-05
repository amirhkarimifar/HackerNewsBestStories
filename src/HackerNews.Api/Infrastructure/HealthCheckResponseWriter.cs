using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HackerNews.Api.Infrastructure;

/// <summary>Writes health reports as JSON so probes and humans can see why a check is not healthy.</summary>
public static class HealthCheckResponseWriter
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(
            new
            {
                status = report.Status.ToString(),
                totalDurationMs = report.TotalDuration.TotalMilliseconds,
                checks = report.Entries.ToDictionary(
                    entry => entry.Key,
                    entry => new
                    {
                        status = entry.Value.Status.ToString(),
                        description = entry.Value.Description,
                        data = entry.Value.Data,
                    }),
            },
            JsonSerializerOptions.Web,
            context.RequestAborted);
    }
}
