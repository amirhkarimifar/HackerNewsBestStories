using HackerNews.Api.Infrastructure;
using HackerNews.Api.Stories;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddBestStories(builder.Configuration);
builder.Services.AddPerClientRateLimiting(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Hacker News Best Stories API";
    document.Info.Description =
        "Returns the best n Hacker News stories in descending order of score. " +
        "Served from an in-memory snapshot, so traffic to this API never translates into traffic to Hacker News.";
    return Task.CompletedTask;
}));

var app = builder.Build();

app.UseExceptionHandler(new ExceptionHandlerOptions
{
    // Malformed input (e.g. ?n=abc) is the client's fault: keep its 400 instead of turning it into a 500.
    StatusCodeSelector = exception => exception is BadHttpRequestException badRequest
        ? badRequest.StatusCode
        : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();
app.UseRateLimiter();

// OpenAPI document (machine-readable, /openapi/v1.json) and Swagger UI (/swagger), which renders it.
// Enabled in every environment: the API is public and read-only, so its description reveals nothing sensitive.
app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "Hacker News Best Stories API v1");
    options.DocumentTitle = "Hacker News Best Stories API";
});
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.MapBestStories();

// Liveness: the process is up. Readiness: the stories have been loaded, so traffic can be routed here.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthCheckResponseWriter.WriteAsync,
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(ServiceCollectionExtensions.ReadinessTag),
    ResponseWriter = HealthCheckResponseWriter.WriteAsync,
});

app.Run();

public partial class Program;
