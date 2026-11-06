using Crawler.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Crawler.Infrastructure.Data;

public class CrawlDbContext : DbContext
{
    public CrawlDbContext(DbContextOptions<CrawlDbContext> options) : base(options) { }

    public DbSet<CrawlJob> CrawlJobs => Set<CrawlJob>();
    public DbSet<CrawledPage> CrawledPages => Set<CrawledPage>();
    public DbSet<PageEdge> PageEdges => Set<PageEdge>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CrawlDbContext).Assembly);
    }
}

