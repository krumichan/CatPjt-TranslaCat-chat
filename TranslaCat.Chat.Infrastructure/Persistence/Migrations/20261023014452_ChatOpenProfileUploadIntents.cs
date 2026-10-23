using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TranslaCat.Chat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChatOpenProfileUploadIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_open_profile_upload_intent",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    member_id = table.Column<long>(type: "bigint", nullable: false),
                    object_key = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false, collation: "utf8mb4_bin"),
                    state = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, collation: "ascii_bin"),
                    created_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    put_confirmed_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    next_attempt_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    attempts = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_open_profile_upload_intent", x => x.id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateIndex(
                name: "idx_chat_open_profile_upload_due",
                table: "chat_open_profile_upload_intent",
                column: "next_attempt_at_utc");

            migrationBuilder.CreateIndex(
                name: "uk_chat_open_profile_upload_key",
                table: "chat_open_profile_upload_intent",
                columns: new[] { "user_id", "object_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_open_profile_upload_intent");
        }
    }
}
