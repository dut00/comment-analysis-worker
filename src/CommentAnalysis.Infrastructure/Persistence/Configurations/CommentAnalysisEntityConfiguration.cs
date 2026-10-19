using System.Text.Json;
using CommentAnalysis.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CommentAnalysis.Infrastructure.Persistence.Configurations;

internal sealed class CommentAnalysisEntityConfiguration : IEntityTypeConfiguration<CommentAnalysisEntity>
{
    private const int EnumMaxLength = 32;

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

        // The default fills rows written before the column existed.
        builder.Property(e => e.ReviewReasons).HasColumnName("review_reasons").HasDefaultValueSql("'{}'");
        builder.Property(e => e.Toxicity).HasColumnName("toxicity");
        builder.Property(e => e.SpamProbability).HasColumnName("spam_probability");
        builder.Property(e => e.PersonalDataProbability).HasColumnName("personal_data_probability");

        builder.Property(e => e.ModelName).HasColumnName("model_name").HasMaxLength(ModelInfo.MaxLength);
        builder.Property(e => e.ModelVersion).HasColumnName("model_version").HasMaxLength(ModelInfo.MaxLength);
        builder.Property(e => e.AnalyzedAt).HasColumnName("analyzed_at");
        builder.Property(e => e.DurationMs).HasColumnName("duration_ms");

        // One analysis per comment and model version: the basis for idempotent processing.
        builder.HasIndex(e => new { e.CommentId, e.ModelVersion })
            .IsUnique()
            .HasDatabaseName(CommentAnalysisEntity.UniqueCommentModelVersionIndex);

        builder.ToTable(table =>
        {
            table.HasCheckConstraint("ck_comment_analyses_confidence_range", "confidence >= 0 AND confidence <= 1");
            table.HasCheckConstraint("ck_comment_analyses_toxicity_range", "toxicity >= 0 AND toxicity <= 1");

            // A comment the model did not evaluate has neither a label nor a confidence.
            table.HasCheckConstraint("ck_comment_analyses_label_confidence", "(label IS NULL) = (confidence IS NULL)");

            // Every manual review has a reason, and no other decision has one.
            table.HasCheckConstraint(
                "ck_comment_analyses_review_reasons",
                "(decision = 'ManualReview') = (cardinality(review_reasons) > 0)");
            table.HasCheckConstraint(
                "ck_comment_analyses_spam_probability_range",
                "spam_probability >= 0 AND spam_probability <= 1");
            table.HasCheckConstraint(
                "ck_comment_analyses_personal_data_probability_range",
                "personal_data_probability >= 0 AND personal_data_probability <= 1");
        });
    }
}
