using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CommentAnalysis.Infrastructure.Persistence.Configurations;

internal sealed class CommentAnalysisEntityConfiguration : IEntityTypeConfiguration<CommentAnalysisEntity>
{
    private const int EnumMaxLength = 32;
    private const int ModelMaxLength = 200;

    public void Configure(EntityTypeBuilder<CommentAnalysisEntity> builder)
    {
        builder.ToTable("comment_analyses");

        builder.HasKey(e => e.Id).HasName("pk_comment_analyses");
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.CommentId).HasColumnName("comment_id");
        builder.Property(e => e.PostId).HasColumnName("post_id");

        builder.Property(e => e.Decision)
            .HasColumnName("decision")
            .HasConversion<string>()
            .HasMaxLength(EnumMaxLength);

        builder.Property(e => e.Label)
            .HasColumnName("label")
            .HasConversion<string>()
            .HasMaxLength(EnumMaxLength);

        builder.Property(e => e.Confidence).HasColumnName("confidence");

        builder.Property(e => e.Probabilities)
            .HasColumnName("probabilities")
            .HasColumnType("jsonb")
            .HasConversion(
                value => JsonSerializer.Serialize(value, JsonSerializerOptions.Default),
                json => JsonSerializer.Deserialize<Dictionary<string, double>>(json, JsonSerializerOptions.Default) ?? new(),
                new ValueComparer<Dictionary<string, double>>(
                    (left, right) => left!.Count == right!.Count && !left.Except(right).Any(),
                    value => value.Aggregate(0, (hash, pair) => HashCode.Combine(hash, pair.Key, pair.Value)),
                    value => new Dictionary<string, double>(value)));

        builder.Property(e => e.ModelName).HasColumnName("model_name").HasMaxLength(ModelMaxLength);
        builder.Property(e => e.ModelVersion).HasColumnName("model_version").HasMaxLength(ModelMaxLength);
        builder.Property(e => e.AnalyzedAt).HasColumnName("analyzed_at");
        builder.Property(e => e.DurationMs).HasColumnName("duration_ms");

        // One analysis per comment and model version: the basis for idempotent processing.
        builder.HasIndex(e => new { e.CommentId, e.ModelVersion })
            .IsUnique()
            .HasDatabaseName("ux_comment_analyses_comment_id_model_version");

        builder.ToTable(table => table.HasCheckConstraint(
            "ck_comment_analyses_confidence_range",
            "confidence >= 0 AND confidence <= 1"));
    }
}
