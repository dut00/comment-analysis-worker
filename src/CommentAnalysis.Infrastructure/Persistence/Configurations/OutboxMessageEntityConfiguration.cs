using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CommentAnalysis.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageEntityConfiguration : IEntityTypeConfiguration<OutboxMessageEntity>
{
    public void Configure(EntityTypeBuilder<OutboxMessageEntity> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(e => e.Id).HasName("pk_outbox_messages");
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(e => e.Type).HasColumnName("type").HasMaxLength(OutboxMessageEntity.TypeMaxLength);
        builder.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");
        builder.Property(e => e.PublishedAt).HasColumnName("published_at");

        // The publisher only ever looks for unpublished rows, oldest first; the partial index stays small.
        builder.HasIndex(e => e.CreatedAt)
            .HasDatabaseName(OutboxMessageEntity.UnpublishedIndex)
            .HasFilter("published_at IS NULL");

        // Cleanup deletes published rows older than the retention period.
        builder.HasIndex(e => e.PublishedAt)
            .HasDatabaseName(OutboxMessageEntity.PublishedIndex)
            .HasFilter("published_at IS NOT NULL");
    }
}
