using Crawler.Domain.Entities;
using Crawler.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crawler.Infrastructure.Data.EntityConfigurations;

public class CrawledPageConfiguration : IEntityTypeConfiguration<CrawledPage>
{
    public void Configure(EntityTypeBuilder<CrawledPage> builder)
    {
        builder.ToTable("crawled_pages");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id");
        builder.Property(p => p.JobId).HasColumnName("job_id");
        builder.Property(p => p.NormalizedUrl).HasColumnName("normalized_url").IsRequired();
        builder.Property(p => p.Depth).HasColumnName("depth");
        builder.Property(p => p.DomainLinkRatio).HasColumnName("domain_link_ratio");
        builder.Property(p => p.TotalLinks).HasColumnName("total_links");
        builder.Property(p => p.InternalLinks).HasColumnName("internal_links");
        builder.Property(p => p.Status).HasColumnName("status")
            .HasConversion<string>()
            .IsRequired();
        builder.Property(p => p.Error).HasColumnName("error");
        builder.Property(p => p.CrawledAt).HasColumnName("crawled_at");

        builder.HasIndex(p => new { p.JobId, p.NormalizedUrl })
            .HasDatabaseName("ix_crawled_pages_job_id_normalized_url")
            .IsUnique();

        builder.HasIndex(p => p.JobId)
            .HasDatabaseName("ix_crawled_pages_job_id");

        builder.HasMany(p => p.OutgoingEdges)
            .WithOne(e => e.ParentPage)
            .HasForeignKey(e => e.ParentPageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

