using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TranslaCat.Chat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MobileMessagePushOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_push_outbox",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    message_id = table.Column<long>(type: "bigint", nullable: false),
                    room_id = table.Column<long>(type: "bigint", nullable: false),
                    sender_user_id = table.Column<long>(type: "bigint", nullable: false),
                    recipient_user_id = table.Column<long>(type: "bigint", nullable: false),
                    event_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "ascii_bin"),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    due_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    lease_token = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true, collation: "ascii_bin"),
                    lease_expires_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_push_outbox", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_push_outbox_chat_message_message_id",
                        column: x => x.message_id,
                        principalTable: "chat_message",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "chat_push_delivery",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    outbox_id = table.Column<long>(type: "bigint", nullable: false),
                    installation_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "ascii_bin"),
                    account_binding_id = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false, collation: "ascii_bin"),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    token_version = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    ticket_id = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true, collation: "ascii_bin"),
                    receipt_expires_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    due_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    lease_token = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true, collation: "ascii_bin"),
                    lease_expires_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_push_delivery", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_push_delivery_chat_push_outbox_outbox_id",
                        column: x => x.outbox_id,
                        principalTable: "chat_push_outbox",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            // 현재 MySQL provider는 Relational collation annotation을 DDL에 내보내지 않는다.
            // 이 새 테이블만 명시적으로 고정해 opaque 식별자와 lease token의 binary 비교를 보장한다.
            migrationBuilder.Sql("""
                ALTER TABLE `chat_push_outbox` CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
                ALTER TABLE `chat_push_outbox`
                    MODIFY COLUMN `event_id` varchar(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                    MODIFY COLUMN `lease_token` varchar(36) CHARACTER SET ascii COLLATE ascii_bin NULL;
                ALTER TABLE `chat_push_delivery` CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
                ALTER TABLE `chat_push_delivery`
                    MODIFY COLUMN `installation_id` varchar(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                    MODIFY COLUMN `account_binding_id` varchar(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                    MODIFY COLUMN `ticket_id` varchar(128) CHARACTER SET ascii COLLATE ascii_bin NULL,
                    MODIFY COLUMN `lease_token` varchar(36) CHARACTER SET ascii COLLATE ascii_bin NULL;
                """);
            migrationBuilder.CreateIndex(
                name: "idx_chat_push_delivery_due",
                table: "chat_push_delivery",
                columns: new[] { "status", "due_at_utc", "lease_expires_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "uk_chat_push_outbox_installation",
                table: "chat_push_delivery",
                columns: new[] { "outbox_id", "installation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_chat_push_outbox_due",
                table: "chat_push_outbox",
                columns: new[] { "status", "due_at_utc", "lease_expires_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "uk_chat_push_message_recipient",
                table: "chat_push_outbox",
                columns: new[] { "message_id", "recipient_user_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_push_delivery");

            migrationBuilder.DropTable(
                name: "chat_push_outbox");
        }
    }
}
