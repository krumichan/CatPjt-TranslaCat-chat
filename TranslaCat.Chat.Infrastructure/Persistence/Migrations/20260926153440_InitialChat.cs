using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TranslaCat.Chat.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_ai_agent",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    nickname = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    profile_image_object_key = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    profile_background_image_object_key = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    bio = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    original_language_code = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    persona_prompt = table.Column<string>(type: "text", nullable: false),
                    active = table.Column<ulong>(type: "bit(1)", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_ai_agent", x => x.id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "chat_ai_system_setting",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    max_ai_members_per_room = table.Column<int>(type: "int", nullable: false),
                    conversation_response_rate = table.Column<int>(type: "int", nullable: false),
                    conversation_cooldown_seconds = table.Column<int>(type: "int", nullable: false),
                    conversation_min_human_messages_after_ai = table.Column<int>(type: "int", nullable: false),
                    response_delay_enabled = table.Column<ulong>(type: "bit(1)", nullable: false),
                    response_delay_min_millis = table.Column<int>(type: "int", nullable: false),
                    response_delay_max_millis = table.Column<int>(type: "int", nullable: false),
                    revival_first_delay_hours = table.Column<int>(type: "int", nullable: false),
                    revival_second_delay_hours = table.Column<int>(type: "int", nullable: false),
                    revival_third_delay_hours = table.Column<int>(type: "int", nullable: false),
                    revival_allowed_start_time = table.Column<TimeSpan>(type: "time(6)", nullable: false),
                    revival_allowed_end_time = table.Column<TimeSpan>(type: "time(6)", nullable: false),
                    context_max_messages = table.Column<int>(type: "int", nullable: false),
                    context_max_characters = table.Column<int>(type: "int", nullable: false),
                    reply_max_characters = table.Column<int>(type: "int", nullable: false),
                    mention_rate_limit_count = table.Column<int>(type: "int", nullable: false),
                    mention_rate_limit_window_seconds = table.Column<int>(type: "int", nullable: false),
                    created_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_ai_system_setting", x => x.id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "chat_room",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    room_type = table.Column<string>(type: "enum('DIRECT','GROUP','OPEN')", maxLength: 30, nullable: false),
                    source_type = table.Column<string>(type: "enum('AI','FRIEND','MANUAL','OPEN')", maxLength: 30, nullable: false),
                    name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    owner_id = table.Column<long>(type: "bigint", nullable: true),
                    active = table.Column<ulong>(type: "bit(1)", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_room", x => x.id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "user_chat_language_setting",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    original_language_code = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    translation_language_code = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    show_original = table.Column<ulong>(type: "bit(1)", nullable: false),
                    show_translation = table.Column<ulong>(type: "bit(1)", nullable: false),
                    created_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_chat_language_setting", x => x.id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "chat_notification",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    recipient_user_id = table.Column<long>(type: "bigint", nullable: false),
                    notification_type = table.Column<string>(type: "enum('CHAT_INVITATION','OPEN_CHAT_KICKED','OPEN_CHAT_ROLE_CHANGED','OPEN_CHAT_ROOM_CLOSED')", maxLength: 50, nullable: false),
                    chat_room_id = table.Column<long>(type: "bigint", nullable: true),
                    actor_user_id = table.Column<long>(type: "bigint", nullable: true),
                    payload_json = table.Column<string>(type: "text", nullable: false),
                    source_event_key = table.Column<string>(type: "varchar(160)", maxLength: 160, nullable: false),
                    is_read = table.Column<ulong>(type: "bit(1)", nullable: false),
                    read_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_notification", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_notification_chat_room_chat_room_id",
                        column: x => x.chat_room_id,
                        principalTable: "chat_room",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "chat_room_ai_activity",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    chat_room_id = table.Column<long>(type: "bigint", nullable: false),
                    last_human_message_id = table.Column<long>(type: "bigint", nullable: true),
                    last_human_message_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    revival_cycle_version = table.Column<long>(type: "bigint", nullable: false),
                    revival_stage = table.Column<int>(type: "int", nullable: false),
                    last_revival_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    next_revival_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    revival_stopped = table.Column<ulong>(type: "bit(1)", nullable: false),
                    last_revival_ai_member_id = table.Column<long>(type: "bigint", nullable: true),
                    claim_token = table.Column<string>(type: "varchar(36)", maxLength: 36, nullable: true),
                    claim_expires_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_room_ai_activity", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_room_ai_activity_chat_room_chat_room_id",
                        column: x => x.chat_room_id,
                        principalTable: "chat_room",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "chat_room_ai_member",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    chat_room_id = table.Column<long>(type: "bigint", nullable: false),
                    ai_agent_id = table.Column<long>(type: "bigint", nullable: false),
                    active = table.Column<ulong>(type: "bit(1)", nullable: false),
                    joined_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    left_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_room_ai_member", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_room_ai_member_chat_ai_agent_ai_agent_id",
                        column: x => x.ai_agent_id,
                        principalTable: "chat_ai_agent",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_room_ai_member_chat_room_chat_room_id",
                        column: x => x.chat_room_id,
                        principalTable: "chat_room",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "chat_room_ai_setting",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    chat_room_id = table.Column<long>(type: "bigint", nullable: false),
                    disclosure_type = table.Column<string>(type: "enum('PRIVATE','PUBLIC')", maxLength: 20, nullable: false),
                    mention_permission = table.Column<string>(type: "enum('ALL_MEMBERS','OWNER_ADMIN_ONLY')", maxLength: 30, nullable: false),
                    conversation_enabled = table.Column<ulong>(type: "bit(1)", nullable: false),
                    revival_enabled = table.Column<ulong>(type: "bit(1)", nullable: false),
                    created_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_room_ai_setting", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_room_ai_setting_chat_room_chat_room_id",
                        column: x => x.chat_room_id,
                        principalTable: "chat_room",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "chat_room_member",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    chat_room_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    role = table.Column<string>(type: "enum('ADMIN','MEMBER','OWNER')", maxLength: 30, nullable: false),
                    original_language_code = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    translation_language_code = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    show_original = table.Column<ulong>(type: "bit(1)", nullable: false),
                    show_translation = table.Column<ulong>(type: "bit(1)", nullable: false),
                    active = table.Column<ulong>(type: "bit(1)", nullable: false),
                    joined_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    last_read_message_id = table.Column<long>(type: "bigint", nullable: true),
                    last_read_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    left_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_room_member", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_room_member_chat_room_chat_room_id",
                        column: x => x.chat_room_id,
                        principalTable: "chat_room",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "open_chat_room",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    chat_room_id = table.Column<long>(type: "bigint", nullable: false),
                    visibility = table.Column<string>(type: "enum('PUBLIC','UNLISTED')", maxLength: 20, nullable: false),
                    max_member_count = table.Column<int>(type: "int", nullable: false),
                    status = table.Column<string>(type: "enum('ACTIVE','CLOSED')", maxLength: 20, nullable: false),
                    closed_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_open_chat_room", x => x.id);
                    table.ForeignKey(
                        name: "FK_open_chat_room_chat_room_chat_room_id",
                        column: x => x.chat_room_id,
                        principalTable: "chat_room",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "chat_message",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    chat_room_id = table.Column<long>(type: "bigint", nullable: false),
                    sender_user_id = table.Column<long>(type: "bigint", nullable: true),
                    sender_ai_member_id = table.Column<long>(type: "bigint", nullable: true),
                    sender_type = table.Column<string>(type: "enum('AI','SYSTEM','USER')", maxLength: 30, nullable: false),
                    message_type = table.Column<string>(type: "enum('SYSTEM','TEXT')", maxLength: 30, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "enum('DELETED','SENT')", maxLength: 30, nullable: false),
                    ai_request_id = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_message", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_message_chat_room_ai_member_sender_ai_member_id",
                        column: x => x.sender_ai_member_id,
                        principalTable: "chat_room_ai_member",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_message_chat_room_chat_room_id",
                        column: x => x.chat_room_id,
                        principalTable: "chat_room",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "open_chat_ban",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    chat_room_id = table.Column<long>(type: "bigint", nullable: false),
                    target_user_id = table.Column<long>(type: "bigint", nullable: false),
                    target_chat_room_member_id = table.Column<long>(type: "bigint", nullable: false),
                    target_member_code = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    nickname_snapshot = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    profile_image_object_key_snapshot = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    last_joined_at_snapshot = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    target_role_snapshot = table.Column<string>(type: "enum('ADMIN','MEMBER','OWNER')", maxLength: 30, nullable: false),
                    banned_by_member_id = table.Column<long>(type: "bigint", nullable: false),
                    banned_by_role = table.Column<string>(type: "enum('ADMIN','MEMBER','OWNER')", maxLength: 30, nullable: false),
                    banned_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    reason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    released_by_member_id = table.Column<long>(type: "bigint", nullable: true),
                    released_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_open_chat_ban", x => x.id);
                    table.ForeignKey(
                        name: "FK_open_chat_ban_chat_room_chat_room_id",
                        column: x => x.chat_room_id,
                        principalTable: "chat_room",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_open_chat_ban_chat_room_member_banned_by_member_id",
                        column: x => x.banned_by_member_id,
                        principalTable: "chat_room_member",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_open_chat_ban_chat_room_member_released_by_member_id",
                        column: x => x.released_by_member_id,
                        principalTable: "chat_room_member",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_open_chat_ban_chat_room_member_target_chat_room_member_id",
                        column: x => x.target_chat_room_member_id,
                        principalTable: "chat_room_member",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "open_chat_member_profile",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    chat_room_member_id = table.Column<long>(type: "bigint", nullable: false),
                    member_code = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    nickname = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    profile_image_object_key = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    created_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_open_chat_member_profile", x => x.id);
                    table.ForeignKey(
                        name: "FK_open_chat_member_profile_chat_room_member_chat_room_member_id",
                        column: x => x.chat_room_member_id,
                        principalTable: "chat_room_member",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "chat_message_translation",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    chat_message_id = table.Column<long>(type: "bigint", nullable: false),
                    language_code = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    translated_content = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "enum('COMPLETED','FAILED','PENDING')", maxLength: 30, nullable: false),
                    failure_reason = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    completed_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_by = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_message_translation", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_message_translation_chat_message_chat_message_id",
                        column: x => x.chat_message_id,
                        principalTable: "chat_message",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateIndex(
                name: "idx_chat_message_room_created_id",
                table: "chat_message",
                columns: new[] { "chat_room_id", "created_at", "id" });

            migrationBuilder.CreateIndex(
                name: "idx_chat_message_room_id_id",
                table: "chat_message",
                columns: new[] { "chat_room_id", "id" });

            migrationBuilder.CreateIndex(
                name: "idx_chat_message_sender_ai_member_id",
                table: "chat_message",
                column: "sender_ai_member_id");

            migrationBuilder.CreateIndex(
                name: "uk_chat_message_ai_request_id",
                table: "chat_message",
                column: "ai_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_chat_translation_message_id",
                table: "chat_message_translation",
                column: "chat_message_id");

            migrationBuilder.CreateIndex(
                name: "idx_chat_translation_status",
                table: "chat_message_translation",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "uk_chat_message_translation_message_language",
                table: "chat_message_translation",
                columns: new[] { "chat_message_id", "language_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_chat_notification_recipient_created",
                table: "chat_notification",
                columns: new[] { "recipient_user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "idx_chat_notification_recipient_read_id",
                table: "chat_notification",
                columns: new[] { "recipient_user_id", "is_read", "id" });

            migrationBuilder.CreateIndex(
                name: "idx_chat_notification_room_id",
                table: "chat_notification",
                column: "chat_room_id");

            migrationBuilder.CreateIndex(
                name: "uk_chat_notification_recipient_type_source",
                table: "chat_notification",
                columns: new[] { "recipient_user_id", "notification_type", "source_event_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_chat_room_owner_id",
                table: "chat_room",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "idx_chat_room_room_type",
                table: "chat_room",
                column: "room_type");

            migrationBuilder.CreateIndex(
                name: "idx_chat_room_source_type",
                table: "chat_room",
                column: "source_type");

            migrationBuilder.CreateIndex(
                name: "idx_chat_room_ai_activity_claim",
                table: "chat_room_ai_activity",
                column: "claim_expires_at");

            migrationBuilder.CreateIndex(
                name: "idx_chat_room_ai_activity_revival_due",
                table: "chat_room_ai_activity",
                columns: new[] { "revival_stopped", "next_revival_at" });

            migrationBuilder.CreateIndex(
                name: "uk_chat_room_ai_activity_room",
                table: "chat_room_ai_activity",
                column: "chat_room_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_chat_room_ai_member_agent_active",
                table: "chat_room_ai_member",
                columns: new[] { "ai_agent_id", "active" });

            migrationBuilder.CreateIndex(
                name: "idx_chat_room_ai_member_room_active",
                table: "chat_room_ai_member",
                columns: new[] { "chat_room_id", "active" });

            migrationBuilder.CreateIndex(
                name: "uk_chat_room_ai_member_room_agent",
                table: "chat_room_ai_member",
                columns: new[] { "chat_room_id", "ai_agent_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uk_chat_room_ai_setting_room",
                table: "chat_room_ai_setting",
                column: "chat_room_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_chat_room_member_room_active",
                table: "chat_room_member",
                columns: new[] { "chat_room_id", "active" });

            migrationBuilder.CreateIndex(
                name: "idx_chat_room_member_user_active",
                table: "chat_room_member",
                columns: new[] { "user_id", "active" });

            migrationBuilder.CreateIndex(
                name: "uk_chat_room_member_room_user",
                table: "chat_room_member",
                columns: new[] { "chat_room_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_open_chat_ban_member_code",
                table: "open_chat_ban",
                column: "target_member_code");

            migrationBuilder.CreateIndex(
                name: "idx_open_chat_ban_room_active_id",
                table: "open_chat_ban",
                columns: new[] { "chat_room_id", "released_at", "id" });

            migrationBuilder.CreateIndex(
                name: "idx_open_chat_ban_room_user_active",
                table: "open_chat_ban",
                columns: new[] { "chat_room_id", "target_user_id", "released_at" });

            migrationBuilder.CreateIndex(
                name: "IX_open_chat_ban_banned_by_member_id",
                table: "open_chat_ban",
                column: "banned_by_member_id");

            migrationBuilder.CreateIndex(
                name: "IX_open_chat_ban_released_by_member_id",
                table: "open_chat_ban",
                column: "released_by_member_id");

            migrationBuilder.CreateIndex(
                name: "IX_open_chat_ban_target_chat_room_member_id",
                table: "open_chat_ban",
                column: "target_chat_room_member_id");

            migrationBuilder.CreateIndex(
                name: "idx_open_chat_member_profile_nickname",
                table: "open_chat_member_profile",
                column: "nickname");

            migrationBuilder.CreateIndex(
                name: "uk_open_chat_member_profile_code",
                table: "open_chat_member_profile",
                column: "member_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uk_open_chat_member_profile_member",
                table: "open_chat_member_profile",
                column: "chat_room_member_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_open_chat_room_status",
                table: "open_chat_room",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "idx_open_chat_room_visibility_status",
                table: "open_chat_room",
                columns: new[] { "visibility", "status" });

            migrationBuilder.CreateIndex(
                name: "uk_open_chat_room_chat_room",
                table: "open_chat_room",
                column: "chat_room_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_user_chat_language_setting_user",
                table: "user_chat_language_setting",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "uk_user_chat_language_setting_user",
                table: "user_chat_language_setting",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_ai_system_setting");

            migrationBuilder.DropTable(
                name: "chat_message_translation");

            migrationBuilder.DropTable(
                name: "chat_notification");

            migrationBuilder.DropTable(
                name: "chat_room_ai_activity");

            migrationBuilder.DropTable(
                name: "chat_room_ai_setting");

            migrationBuilder.DropTable(
                name: "open_chat_ban");

            migrationBuilder.DropTable(
                name: "open_chat_member_profile");

            migrationBuilder.DropTable(
                name: "open_chat_room");

            migrationBuilder.DropTable(
                name: "user_chat_language_setting");

            migrationBuilder.DropTable(
                name: "chat_message");

            migrationBuilder.DropTable(
                name: "chat_room_member");

            migrationBuilder.DropTable(
                name: "chat_room_ai_member");

            migrationBuilder.DropTable(
                name: "chat_ai_agent");

            migrationBuilder.DropTable(
                name: "chat_room");
        }
    }
}
