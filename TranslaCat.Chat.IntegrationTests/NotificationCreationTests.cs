using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Infrastructure.Persistence.Notifications;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class NotificationCreationTests(ChatMySqlFixture database)
{
    [Fact]
    public async Task Concurrent_duplicate_source_creates_and_publishes_one_committed_activity()
    {
        // 준비
        var room = await database.SeedReadRoomAsync(userId: Random.Shared.NextInt64(6_000_000_000, 7_000_000_000));
        var delivery = new ActivityDelivery(database);
        var request = new ChatActivityNotificationRequest(room.UserId, "synthetic@example.invalid", "CHAT_INVITATION",
            room.RoomId, room.UserId + 1, "{\"roomName\":\"synthetic\"}", "synthetic:" + Guid.NewGuid().ToString("N"), "synthetic-actor@example.invalid");

        // 실행
        var results = await Task.WhenAll(Writer(delivery).CreateAsync(request, default), Writer(delivery).CreateAsync(request, default));

        // 검증
        Assert.Single(results, value => value is not null);
        Assert.Single(delivery.Delivered);
        await using var context = await database.Contexts.CreateDbContextAsync();
        Assert.Equal(1, await context.ChatNotifications.CountAsync(row => row.SourceEventKey == request.SourceEventKey));
    }

    [Fact]
    public async Task Missing_realtime_adapter_does_not_persist_notification()
    {
        // 준비
        var sourceKey = "synthetic:" + Guid.NewGuid().ToString("N");
        var request = new ChatActivityNotificationRequest(123, "synthetic@example.invalid", "OPEN_CHAT_ROOM_CLOSED", null,
            null, "{}", sourceKey, "synthetic-actor@example.invalid");

        // 실행 / 검증
        await Assert.ThrowsAsync<ChatNotificationDependencyUnavailableException>(() => Writer(null).CreateAsync(request, default));
        await using var context = await database.Contexts.CreateDbContextAsync();
        Assert.False(await context.ChatNotifications.AnyAsync(row => row.SourceEventKey == sourceKey));
    }

    private EfChatActivityNotificationWriter Writer(IChatActivityNotificationDelivery? delivery)
    {
        return new(database.Contexts, delivery, () => ChatMySqlFixture.Epoch);
    }

    private sealed class ActivityDelivery(ChatMySqlFixture database) : IChatActivityNotificationDelivery
    {
        public System.Collections.Concurrent.ConcurrentQueue<long> Delivered { get; } = new();
        public Task ValidateAvailabilityAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async Task DeliverAsync(string recipientEmail, ChatNotificationActivity notification, DateTime occurredAt, CancellationToken cancellationToken)
        {
            await using var context = await database.Contexts.CreateDbContextAsync(cancellationToken);
            Assert.True(await context.ChatNotifications.AnyAsync(row => row.Id == notification.Id, cancellationToken));
            Assert.Equal("synthetic@example.invalid", recipientEmail);
            Delivered.Enqueue(notification.Id);
        }
    }
}
