using Crawler.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Crawler.Infrastructure.Data.EntityConfigurations;

public class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("processed_messages");

        builder.HasKey(m => m.MessageId);
        builder.Property(m => m.MessageId).HasColumnName("message_id");
        builder.Property(m => m.ProcessedAt).HasColumnName("processed_at");
    }
}

