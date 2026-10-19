using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommentAnalysis.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewReasonsAndSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "personal_data_probability",
                table: "comment_analyses",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "review_reasons",
                table: "comment_analyses",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            // Before review reasons existed, low confidence was the only way to manual review.
            migrationBuilder.Sql("UPDATE comment_analyses SET review_reasons = '{LowConfidence}' WHERE decision = 'ManualReview';");

            migrationBuilder.AddColumn<double>(
                name: "spam_probability",
                table: "comment_analyses",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "toxicity",
                table: "comment_analyses",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_comment_analyses_personal_data_probability_range",
                table: "comment_analyses",
                sql: "personal_data_probability >= 0 AND personal_data_probability <= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_comment_analyses_review_reasons",
                table: "comment_analyses",
                sql: "(decision = 'ManualReview') = (cardinality(review_reasons) > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_comment_analyses_spam_probability_range",
                table: "comment_analyses",
                sql: "spam_probability >= 0 AND spam_probability <= 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_comment_analyses_toxicity_range",
                table: "comment_analyses",
                sql: "toxicity >= 0 AND toxicity <= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_comment_analyses_personal_data_probability_range",
                table: "comment_analyses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_comment_analyses_review_reasons",
                table: "comment_analyses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_comment_analyses_spam_probability_range",
                table: "comment_analyses");

            migrationBuilder.DropCheckConstraint(
                name: "ck_comment_analyses_toxicity_range",
                table: "comment_analyses");

            migrationBuilder.DropColumn(
                name: "personal_data_probability",
                table: "comment_analyses");

            migrationBuilder.DropColumn(
                name: "review_reasons",
                table: "comment_analyses");

            migrationBuilder.DropColumn(
                name: "spam_probability",
                table: "comment_analyses");

            migrationBuilder.DropColumn(
                name: "toxicity",
                table: "comment_analyses");
        }
    }
}
