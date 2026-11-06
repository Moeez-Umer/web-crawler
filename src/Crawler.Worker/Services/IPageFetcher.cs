namespace Crawler.Worker.Services;

/// <summary>Fetches an HTTP page and returns a typed result.</summary>
public interface IPageFetcher
{
    Task<PageFetchResult> FetchAsync(string url, CancellationToken cancellationToken = default);
}

