using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CommentAnalysis.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxPublishedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_published_at",
                table: "outbox_messages",
                column: "published_at",
                filter: "published_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_messages_published_at",
                table: "outbox_messages");
        }
    }
}
