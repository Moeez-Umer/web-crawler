namespace Crawler.Domain.Entities;

public class PageEdge
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public Guid ParentPageId { get; set; }
    public string ChildUrl { get; set; } = string.Empty;

    public CrawlJob Job { get; set; } = null!;
    public CrawledPage ParentPage { get; set; } = null!;
}

