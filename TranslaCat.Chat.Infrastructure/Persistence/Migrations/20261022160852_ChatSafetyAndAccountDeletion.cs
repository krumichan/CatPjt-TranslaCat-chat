using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TranslaCat.Chat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChatSafetyAndAccountDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_account_deletion",
                columns: table => new
                {
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    operation_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "ascii_bin"),
                    requested_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    local_completed_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    completed_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_account_deletion", x => x.user_id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "chat_account_deletion_object",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    object_key = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    due_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    completed_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_account_deletion_object", x => x.id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "chat_report",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    room_id = table.Column<long>(type: "bigint", nullable: false),
                    reporter_user_id = table.Column<long>(type: "bigint", nullable: true),
                    client_request_id = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: false, collation: "ascii_bin"),
                    request_fingerprint = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false, collation: "ascii_bin"),
                    target_type = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    message_id = table.Column<long>(type: "bigint", nullable: true),
                    target_user_id = table.Column<long>(type: "bigint", nullable: true),
                    target_member_id = table.Column<long>(type: "bigint", nullable: true),
                    reason = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    detail = table.Column<string>(type: "text", nullable: true),
                    evidence_content = table.Column<string>(type: "text", nullable: true),
                    evidence_sender_display = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    review_note = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    evidence_expires_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    evidence_purged_at = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_report", x => x.id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "chat_report_review",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    report_id = table.Column<long>(type: "bigint", nullable: false),
                    reviewer_user_id = table.Column<long>(type: "bigint", nullable: true),
                    previous_status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_report_review", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_report_review_chat_report_report_id",
                        column: x => x.report_id,
                        principalTable: "chat_report",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateIndex(
                name: "uk_chat_account_deletion_operation",
                table: "chat_account_deletion",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_chat_deletion_object_due",
                table: "chat_account_deletion_object",
                columns: new[] { "completed_at_utc", "due_at_utc" });

            migrationBuilder.CreateIndex(
                name: "uk_chat_deletion_object",
                table: "chat_account_deletion_object",
                columns: new[] { "user_id", "object_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_chat_report_evidence_due",
                table: "chat_report",
                columns: new[] { "evidence_purged_at", "evidence_expires_at" });

            migrationBuilder.CreateIndex(
                name: "idx_chat_report_status_id",
                table: "chat_report",
                columns: new[] { "status", "id" });

            migrationBuilder.CreateIndex(
                name: "uk_chat_report_request",
                table: "chat_report",
                columns: new[] { "reporter_user_id", "client_request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_chat_report_review_history",
                table: "chat_report_review",
                columns: new[] { "report_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_account_deletion");

            migrationBuilder.DropTable(
                name: "chat_account_deletion_object");

            migrationBuilder.DropTable(
                name: "chat_report_review");

            migrationBuilder.DropTable(
                name: "chat_report");
        }
    }
}
