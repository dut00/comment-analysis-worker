using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommentAnalysis.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowAnalysesWithoutPrediction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "label",
                table: "comment_analyses",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<double>(
                name: "confidence",
                table: "comment_analyses",
                type: "double precision",
                nullable: true,
                oldClrType: typeof(double),
                oldType: "double precision");

            migrationBuilder.AddCheckConstraint(
                name: "ck_comment_analyses_label_confidence",
                table: "comment_analyses",
                sql: "(label IS NULL) = (confidence IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Lossy: analyses without a prediction (too long comments) cannot be represented without these columns.
            migrationBuilder.Sql("DELETE FROM comment_analyses WHERE label IS NULL;");

            migrationBuilder.DropCheckConstraint(
                name: "ck_comment_analyses_label_confidence",
                table: "comment_analyses");

            migrationBuilder.AlterColumn<string>(
                name: "label",
                table: "comment_analyses",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<double>(
                name: "confidence",
                table: "comment_analyses",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0,
                oldClrType: typeof(double),
                oldType: "double precision",
                oldNullable: true);
        }
    }
}
