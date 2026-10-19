using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommentAnalysis.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "comment_analyses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    comment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    label = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    probabilities = table.Column<string>(type: "jsonb", nullable: false),
                    model_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    model_version = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    analyzed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    duration_ms = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_comment_analyses", x => x.id);
                    table.CheckConstraint("ck_comment_analyses_confidence_range", "confidence >= 0 AND confidence <= 1");
                });

            migrationBuilder.CreateIndex(
                name: "ux_comment_analyses_comment_id_model_version",
                table: "comment_analyses",
                columns: new[] { "comment_id", "model_version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "comment_analyses");
        }
    }
}
