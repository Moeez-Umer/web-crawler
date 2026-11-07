using Serilog.Context;

namespace Crawler.Api.Middleware;

/// <summary>
/// Reads X-Correlation-Id from the request header (or generates a new GUID),
/// echoes it on the response header, and enriches the Serilog LogContext.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string correlationId = context.Request.Headers.TryGetValue(HeaderName, out var values) &&
                               !string.IsNullOrWhiteSpace(values.FirstOrDefault())
            ? values.First()!.Trim()
            : Guid.NewGuid().ToString("D");

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("correlationId", correlationId))
        {
            await _next(context);
        }
    }
}

