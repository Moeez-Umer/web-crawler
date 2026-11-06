namespace Crawler.Worker.Services;

/// <summary>Typed result returned by IPageFetcher.</summary>
public abstract record PageFetchResult
{
    private PageFetchResult() { }

    /// <summary>HTML was fetched successfully. FinalUrl reflects any redirects.</summary>
    public sealed record Success(string Html, Uri FinalUrl) : PageFetchResult;

    /// <summary>
    /// Permanent failure (404, 403, non-HTML content-type, body-too-large, bad URL).
    /// Do NOT retry; record the page as Failed and ACK.
    /// </summary>
    public sealed record Permanent(string Reason) : PageFetchResult;

    /// <summary>
    /// Transient failure (timeout, network error, 5xx, 429).
    /// Eligible for retry via crawl.retry queue.
    /// </summary>
    public sealed record Transient(string Reason) : PageFetchResult;
}

