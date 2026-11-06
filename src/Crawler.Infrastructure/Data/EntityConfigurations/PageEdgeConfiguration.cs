using Crawler.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crawler.Infrastructure.Data.EntityConfigurations;

public class PageEdgeConfiguration : IEntityTypeConfiguration<PageEdge>
{
    public void Configure(EntityTypeBuilder<PageEdge> builder)
    {
        builder.ToTable("page_edges");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.JobId).HasColumnName("job_id");
        builder.Property(e => e.ParentPageId).HasColumnName("parent_page_id");
        builder.Property(e => e.ChildUrl).HasColumnName("child_url").IsRequired();

        builder.HasIndex(e => new { e.JobId, e.ParentPageId, e.ChildUrl })
            .HasDatabaseName("ix_page_edges_job_id_parent_page_id_child_url")
            .IsUnique();

        builder.HasIndex(e => new { e.JobId, e.ParentPageId })
            .HasDatabaseName("ix_page_edges_job_id_parent_page_id");
    }
}

