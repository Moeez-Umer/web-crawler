namespace Crawler.Domain.Enums;

public enum PageStatus
{
    /// <summary>Row inserted at discovery time; its own task has not finished yet (D-019).</summary>
    Pending,
    Crawled,
    Failed
}
