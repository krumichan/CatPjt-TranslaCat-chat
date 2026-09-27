using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Infrastructure.Persistence.Notifications;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class NotificationPersistenceTests(ChatMySqlFixture database)
{
    [Fact]
    public async Task Summary_and_chat_page_preserve_sent_visible_unread_rules_and_latest_self_message()
    {
        // 준비
        var room = await database.SeedReadRoomAsync(userId: NewUserId());
        long selfId;
        await using (var context = await database.Contexts.CreateDbContextAsync())
        {
            var ai = ChatMySqlFixture.Message(room.RoomId, null, ChatMySqlFixture.Epoch.AddSeconds(3));
            ai.SenderType = "AI";
            var self = ChatMySqlFixture.Message(room.RoomId, room.UserId, ChatMySqlFixture.Epoch.AddSeconds(4));
            var system = ChatMySqlFixture.Message(room.RoomId, room.UserId + 1, ChatMySqlFixture.Epoch.AddSeconds(5));
            system.MessageType = "SYSTEM";
            var removed = ChatMySqlFixture.Message(room.RoomId, room.UserId + 1, ChatMySqlFixture.Epoch.AddSeconds(6));
            removed.DeletedAt = ChatMySqlFixture.Epoch;
            var beforeJoin = ChatMySqlFixture.Message(room.RoomId, room.UserId + 1, ChatMySqlFixture.Epoch.AddSeconds(-1));
            context.ChatMessages.AddRange(ai, self, system, removed, beforeJoin);
            context.ChatNotifications.AddRange(Activity(room.UserId, room.RoomId), Activity(room.UserId + 5, room.RoomId));
            await context.SaveChangesAsync();
            selfId = self.Id;
        }
        var store = Store();

        // 실행
        var summary = await store.GetSummaryAsync(room.UserId, default);
        var page = await store.GetUnreadChatsAsync(room.UserId, null, 21, default);

        // 검증: AI는 unread에 포함하고 latest preview는 자기 메시지도 포함한다.
        Assert.Equal(new ChatNotificationSummary(3, 1, 1), summary);
        Assert.Equal(4, summary.TotalAttentionCount);
        var item = Assert.Single(page);
        Assert.Equal(3, item.UnreadCount);
        Assert.Equal(room.FirstId, item.FirstUnreadMessageId);
        Assert.Equal(selfId, item.LatestMessageId);
        Assert.Empty(await store.GetUnreadChatsAsync(room.UserId, selfId, 21, default));
    }

    [Fact]
    public async Task Activities_page_is_recipient_scoped_descending_and_unread_filter_ignores_deleted_rows()
    {
        // 준비
        var room = await database.SeedReadRoomAsync(userId: NewUserId());
        var records = Enumerable.Range(0, 4).Select(_ => Activity(room.UserId, room.RoomId)).ToArray();
        records[1].IsRead = true;
        records[2].DeletedAt = ChatMySqlFixture.Epoch;
        await using (var context = await database.Contexts.CreateDbContextAsync())
        {
            context.ChatNotifications.AddRange(records);
            context.ChatNotifications.Add(Activity(room.UserId + 1, room.RoomId));
            await context.SaveChangesAsync();
        }
        var service = Service();

        // 실행
        var first = await service.GetActivitiesAsync(room.UserId, size: 2);
        var second = await service.GetActivitiesAsync(room.UserId, cursorId: first.NextCursorId, size: 2);
        var unread = await service.GetActivitiesAsync(room.UserId, onlyUnread: true);

        // 검증
        Assert.Equal(new[] { records[3].Id, records[1].Id }, first.Items.Select(item => item.Id));
        Assert.True(first.HasNext);
        Assert.Equal(records[1].Id, first.NextCursorId);
        Assert.Equal(records[0].Id, Assert.Single(second.Items).Id);
        Assert.False(second.HasNext);
        Assert.Equal(new[] { records[3].Id, records[0].Id }, unread.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task Concurrent_read_and_read_all_preserve_first_timestamp_and_never_modify_chat_cursor()
    {
        // 준비
        var room = await database.SeedReadRoomAsync(userId: NewUserId());
        var first = Activity(room.UserId, room.RoomId);
        var second = Activity(room.UserId, room.RoomId);
        await using (var context = await database.Contexts.CreateDbContextAsync())
        {
            context.ChatNotifications.AddRange(first, second);
            await context.SaveChangesAsync();
        }
        var earlier = ChatMySqlFixture.Epoch.AddSeconds(10);
        var later = earlier.AddSeconds(1);

        // 실행: 각 호출은 별도 실제 MySQL connection/transaction을 사용한다.
        var responses = await Task.WhenAll(Store().MarkReadAsync(room.UserId, first.Id, earlier, default),
            Store().MarkReadAsync(room.UserId, first.Id, later, default));
        var updated = await Store().MarkAllReadAsync(room.UserId, later.AddSeconds(1), default);

        // 검증
        Assert.Equal(responses[0]!.ReadAt, responses[1]!.ReadAt);
        Assert.Contains(responses[0]!.ReadAt, new DateTime?[] { earlier, later });
        Assert.Equal(1, updated);
        Assert.Equal(0, await Store().MarkAllReadAsync(room.UserId, later.AddMinutes(1), default));
        await using var verification = await database.Contexts.CreateDbContextAsync();
        var saved = await verification.ChatNotifications.SingleAsync(row => row.Id == first.Id);
        Assert.Equal(responses[0]!.ReadAt, saved.ReadAt);
        Assert.Null((await verification.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId)).LastReadMessageId);
    }

    [Fact]
    public async Task Already_read_null_timestamp_is_preserved_and_other_recipient_cannot_read()
    {
        // 준비
        var room = await database.SeedReadRoomAsync(userId: NewUserId());
        var activity = Activity(room.UserId, room.RoomId);
        activity.IsRead = true;
        await using (var context = await database.Contexts.CreateDbContextAsync())
        {
            context.ChatNotifications.Add(activity);
            await context.SaveChangesAsync();
        }

        // 실행
        var own = await Service().MarkReadAsync(room.UserId, activity.Id);
        var error = await Assert.ThrowsAsync<ChatNotificationException>(() => Service().MarkReadAsync(room.UserId + 1, activity.Id));

        // 검증
        Assert.True(own.IsRead);
        Assert.Null(own.ReadAt);
        Assert.Equal("CHAT_NOTIFICATION_NOT_FOUND", error.Code);
    }

    private EfChatNotificationStore Store()
    {
        return new(database.Contexts);
    }

    private ChatNotificationService Service()
    {
        return new(Store(), null, () => ChatMySqlFixture.Epoch.AddMinutes(1));
    }

    private static long NewUserId()
    {
        return Random.Shared.NextInt64(2_000_000_001, 4_000_000_000);
    }

    private static ChatNotificationEntity Activity(long userId, long roomId)
    {
        return new()
        {
            RecipientUserId = userId,
            ChatRoomId = roomId,
            NotificationType = "CHAT_INVITATION",
            PayloadJson = "{\"roomName\":\"합성 방\"}",
            SourceEventKey = "synthetic:" + Guid.NewGuid().ToString("N"),
            CreatedAt = ChatMySqlFixture.Epoch
        };
    }
}
