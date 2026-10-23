using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.Data.MySqlClient;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Api.Safety;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class NotificationPreferenceMigrationTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task Additive_upgrade_preserves_existing_member_and_unread_notification_with_default_receive_policy()
    {
        // 준비: 실행 소유 manifest에 새 catalog를 먼저 등록한다. 운영·기존 catalog는 사용하지 않는다.
        var manifestPath = Environment.GetEnvironmentVariable("CHAT_TEST_MANIFEST")!;
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        Assert.True(manifest["testMode"]!.GetValue<bool>());
        Assert.Equal(Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID"), manifest["runId"]!.GetValue<string>());
        string catalog = "translacat_chat_test_" + Guid.NewGuid().ToString("N");
        manifest["catalogs"]!.AsArray().Add(catalog);
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(new() { WriteIndented = true }));
        await using var admin = new MySqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await using (var create = admin.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE `{catalog}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci";
            await create.ExecuteNonQueryAsync();
        }
        var connection = new MySqlConnectionStringBuilder(fixture.ConnectionString) { Database = catalog };
        await using var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>()
            .UseMySQL(ChatDatabaseTarget.Validate(connection.ConnectionString, true)).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261022041228_OpenInvitationDurablePush");
        var room = new ChatRoomEntity { RoomType = "GROUP", CreatedAt = ChatMySqlFixture.Epoch, UpdatedAt = ChatMySqlFixture.Epoch };
        db.ChatRooms.Add(room);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO chat_room_member (chat_room_id, user_id, role, show_original, show_translation, active, joined_at, created_at, updated_at)
            VALUES ({room.Id}, 300000001, 'MEMBER', b'1', b'1', b'1', {ChatMySqlFixture.Epoch}, {ChatMySqlFixture.Epoch}, {ChatMySqlFixture.Epoch})
            """);
        var notification = new ChatNotificationEntity { RecipientUserId = 300000001, ActorUserId = 300000002,
            ChatRoomId = room.Id, NotificationType = "CHAT_INVITATION", PayloadJson = "{}",
            SourceEventKey = "synthetic-upgrade", CreatedAt = ChatMySqlFixture.Epoch };
        db.ChatNotifications.Add(notification);
        await db.SaveChangesAsync();
        var before = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        await Assert.ThrowsAnyAsync<Exception>(() => ChatAccountDeletionSchema.ValidateAsync(db, default));

        // 실행: 이전 릴리스에 존재하던 행을 가진 상태에서 additive upgrade하고 재적용한다.
        await db.Database.MigrateAsync();
        var after = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
        await db.Database.MigrateAsync();

        // 검증: 기존 알림 migration 뒤 안전 정책/계정 gate 2개도 한 번씩만 추가된다.
        Assert.Equal(before.Length + 4, after.Length);
        Assert.Equal(new[] { "20261022071017_ChatNotificationPreferences", "20261022160852_ChatSafetyAndAccountDeletion", "20261022164144_ChatAccountWriteFence", "20261023014452_ChatOpenProfileUploadIntents" }, after.Skip(before.Length));
        Assert.Equal(after, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        var member = await db.ChatRoomMembers.AsNoTracking().SingleAsync();
        Assert.False(member.NotificationMuted);
        Assert.Null(member.NotificationEnabledSinceUtc);
        Assert.True(member.Active);
        Assert.Null(member.LastReadMessageId);
        var activity = await db.ChatNotifications.AsNoTracking().SingleAsync();
        Assert.Equal(notification.Id, activity.Id);
        Assert.False(activity.IsRead);
        Assert.Null(activity.ReadAt);
        Assert.False(await db.ChatNotificationPreferences.AnyAsync());
        Assert.False(await db.ChatAccountDeletions.AnyAsync());
        Assert.False(await db.ChatAccountWriteGates.AnyAsync());
        Assert.False(await db.ChatOpenProfileUploadIntents.AnyAsync());
        await ChatAccountDeletionSchema.ValidateAsync(db, default);

        // 테이블이 있어도 컬럼 하나가 빠지면 ready가 될 수 없다. 이 실행에서 만든 합성 catalog만 변경한다.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE chat_open_profile_upload_intent RENAME COLUMN attempts TO synthetic_missing_attempts");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => ChatAccountDeletionSchema.ValidateAsync(db, default));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE chat_open_profile_upload_intent RENAME COLUMN synthetic_missing_attempts TO attempts");
        }
        await ChatAccountDeletionSchema.ValidateAsync(db, default);
    }
}
