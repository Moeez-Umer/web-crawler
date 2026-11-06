using Crawler.Domain.Entities;
using Crawler.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crawler.Infrastructure.Data.EntityConfigurations;

public class CrawlJobConfiguration : IEntityTypeConfiguration<CrawlJob>
{
    public void Configure(EntityTypeBuilder<CrawlJob> builder)
    {
        builder.ToTable("crawl_jobs");

        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).HasColumnName("id");
        builder.Property(j => j.Url).HasColumnName("url").IsRequired();
        builder.Property(j => j.MaxDepth).HasColumnName("max_depth");
        builder.Property(j => j.MaxPages).HasColumnName("max_pages");
        builder.Property(j => j.Status).HasColumnName("status")
            .HasConversion<string>()
            .IsRequired();
        builder.Property(j => j.CreatedAt).HasColumnName("created_at");
        builder.Property(j => j.StartedAt).HasColumnName("started_at");
        builder.Property(j => j.CompletedAt).HasColumnName("completed_at");
        builder.Property(j => j.FailureReason).HasColumnName("failure_reason");
        builder.Property(j => j.PendingTasks).HasColumnName("pending_tasks");
        builder.Property(j => j.PagesCrawled).HasColumnName("pages_crawled");

        builder.HasIndex(j => j.CreatedAt)
            .HasDatabaseName("ix_crawl_jobs_created_at")
            .IsDescending();

        builder.HasMany(j => j.Pages)
            .WithOne(p => p.Job)
            .HasForeignKey(p => p.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(j => j.Edges)
            .WithOne(e => e.Job)
            .HasForeignKey(e => e.JobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

