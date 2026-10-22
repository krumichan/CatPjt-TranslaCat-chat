using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TranslaCat.Chat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OpenInvitationDurablePush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_open_invitation_sender_gate",
                columns: table => new
                {
                    actor_user_id = table.Column<long>(type: "bigint", nullable: false),
                    touched_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_open_invitation_sender_gate", x => x.actor_user_id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "open_invitation_push_outbox",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    notification_id = table.Column<long>(type: "bigint", nullable: false),
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
                    table.PrimaryKey("PK_open_invitation_push_outbox", x => x.id);
                    table.ForeignKey(
                        name: "FK_open_invitation_push_outbox_chat_notification_notification_id",
                        column: x => x.notification_id,
                        principalTable: "chat_notification",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "open_invitation_push_delivery",
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
                    table.PrimaryKey("PK_open_invitation_push_delivery", x => x.id);
                    table.ForeignKey(
                        name: "FK_open_invitation_push_delivery_open_invitation_push_outbox_ou~",
                        column: x => x.outbox_id,
                        principalTable: "open_invitation_push_outbox",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            // MySQL provider는 collation annotation을 DDL에 내보내지 않아 opaque token의 binary 비교를 명시한다.
            migrationBuilder.Sql("""
                ALTER TABLE `open_invitation_push_outbox` CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
                ALTER TABLE `open_invitation_push_outbox`
                    MODIFY COLUMN `event_id` varchar(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                    MODIFY COLUMN `lease_token` varchar(36) CHARACTER SET ascii COLLATE ascii_bin NULL;
                ALTER TABLE `open_invitation_push_delivery` CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
                ALTER TABLE `open_invitation_push_delivery`
                    MODIFY COLUMN `installation_id` varchar(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                    MODIFY COLUMN `account_binding_id` varchar(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                    MODIFY COLUMN `ticket_id` varchar(128) CHARACTER SET ascii COLLATE ascii_bin NULL,
                    MODIFY COLUMN `lease_token` varchar(36) CHARACTER SET ascii COLLATE ascii_bin NULL;
                """);
            migrationBuilder.CreateIndex(name: "idx_chat_notification_actor_source", table: "chat_notification",
                columns: new[] { "actor_user_id", "source_event_key" });
            migrationBuilder.CreateIndex(name: "idx_open_invitation_push_sender_time", table: "open_invitation_push_outbox",
                columns: new[] { "sender_user_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "idx_open_invitation_push_delivery_due",
                table: "open_invitation_push_delivery",
                columns: new[] { "status", "due_at_utc", "lease_expires_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "uk_open_invitation_push_outbox_installation",
                table: "open_invitation_push_delivery",
                columns: new[] { "outbox_id", "installation_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_open_invitation_push_outbox_due",
                table: "open_invitation_push_outbox",
                columns: new[] { "status", "due_at_utc", "lease_expires_at_utc", "id" });

            migrationBuilder.CreateIndex(
                name: "uk_open_invitation_push_event",
                table: "open_invitation_push_outbox",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uk_open_invitation_push_notification",
                table: "open_invitation_push_outbox",
                column: "notification_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "idx_chat_notification_actor_source", table: "chat_notification");
            migrationBuilder.DropTable(
                name: "chat_open_invitation_sender_gate");

            migrationBuilder.DropTable(
                name: "open_invitation_push_delivery");

            migrationBuilder.DropTable(
                name: "open_invitation_push_outbox");
        }
    }
}
