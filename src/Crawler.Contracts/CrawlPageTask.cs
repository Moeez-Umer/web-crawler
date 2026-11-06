namespace Crawler.Contracts;

public record CrawlPageTask(
    Guid MessageId,
    Guid JobId,
    string Url,
    string? ParentUrl,
    int Depth,
    int MaxDepth,
    string RootHost,
    int Attempt);

