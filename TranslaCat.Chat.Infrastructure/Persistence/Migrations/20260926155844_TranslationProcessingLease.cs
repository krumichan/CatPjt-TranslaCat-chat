using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TranslaCat.Chat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TranslationProcessingLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "processing_expires_at",
                table: "chat_message_translation",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "processing_token",
                table: "chat_message_translation",
                type: "varchar(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_chat_translation_processing_due",
                table: "chat_message_translation",
                columns: new[] { "status", "deleted_at", "processing_expires_at", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "idx_chat_translation_processing_due",
                table: "chat_message_translation");

            migrationBuilder.DropColumn(
                name: "processing_expires_at",
                table: "chat_message_translation");

            migrationBuilder.DropColumn(
                name: "processing_token",
                table: "chat_message_translation");
        }
    }
}
