using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TranslaCat.Chat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChatNotificationPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "notification_enabled_since_utc",
                table: "chat_room_member",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<ulong>(
                name: "notification_muted",
                table: "chat_room_member",
                type: "bit(1)",
                nullable: false,
                defaultValue: 0ul);

            migrationBuilder.CreateTable(
                name: "chat_notification_preference",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    push_enabled = table.Column<ulong>(type: "bit(1)", nullable: false),
                    enabled_since_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_notification_preference", x => x.user_id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_notification_preference");

            migrationBuilder.DropColumn(
                name: "notification_enabled_since_utc",
                table: "chat_room_member");

            migrationBuilder.DropColumn(
                name: "notification_muted",
                table: "chat_room_member");
        }
    }
}
