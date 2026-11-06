using Crawler.Domain.Enums;

namespace Crawler.Domain.Entities;

public class CrawlJob
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public int MaxDepth { get; set; }
    public int MaxPages { get; set; }
    public JobStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FailureReason { get; set; }
    public int PendingTasks { get; set; }
    public int PagesCrawled { get; set; }

    public ICollection<CrawledPage> Pages { get; set; } = new List<CrawledPage>();
    public ICollection<PageEdge> Edges { get; set; } = new List<PageEdge>();
}

