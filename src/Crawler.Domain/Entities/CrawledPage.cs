using Crawler.Domain.Enums;

namespace Crawler.Domain.Entities;

public class CrawledPage
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string NormalizedUrl { get; set; } = string.Empty;
    public int Depth { get; set; }
    public double DomainLinkRatio { get; set; }
    public int TotalLinks { get; set; }
    public int InternalLinks { get; set; }
    public PageStatus Status { get; set; }
    public string? Error { get; set; }
    public DateTime CrawledAt { get; set; }

    public CrawlJob Job { get; set; } = null!;
    public ICollection<PageEdge> OutgoingEdges { get; set; } = new List<PageEdge>();
}

