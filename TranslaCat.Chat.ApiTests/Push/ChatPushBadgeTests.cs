using System.Text.Json;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Infrastructure.Push;

namespace TranslaCat.Chat.ApiTests.Push;

public sealed class ChatPushBadgeTests
{
    [Theory]
    [InlineData(2L, 3L, 5)]
    [InlineData(0L, 0L, 0)]
    [InlineData(-10L, 3L, 0)]
    [InlineData(long.MaxValue, long.MaxValue, int.MaxValue)]
    public void Count_uses_message_plus_activity_once_and_clamps_safely(long messages, long activities, int expected)
    {
        // 준비 / 실행
        int count = ChatPushBadge.Count(new(messages, 999, activities));

        // 검증: 안읽은 방 수를 다시 합산하지 않는다.
        Assert.Equal(expected, count);
    }

    [Fact]
    public async Task Failed_summary_omits_badge_instead_of_claiming_zero_and_cancellation_propagates()
    {
        // 준비
        var store = new BadgeSummaryStore { Error = new InvalidOperationException("synthetic") };

        // 실행 / 검증
        Assert.Null(await ChatPushBadge.ReadAsync(store, 22, default));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        store.Error = new OperationCanceledException(cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChatPushBadge.ReadAsync(store, 22, cancel.Token));
    }

    [Fact]
    public void Both_payload_versions_keep_badge_outside_identity_and_omit_unavailable_count()
    {
        // 준비
        var message = new ExpoPushPayload("synthetic", "TranslaCat", "message", "chat-messages", "default",
            new(1, "event", "3", "7", "binding"));
        var invitation = new ExpoOpenInvitationPushPayload("synthetic", "TranslaCat", "invite", "chat-messages", "default",
            new(2, "OPEN_INVITATION", "event", "11", "3", "binding"));

        // 실행 / 검증
        using var without = JsonDocument.Parse(ExpoChatPushClient.SerializePayload(message));
        Assert.False(without.RootElement.TryGetProperty("badge", out _));
        using var v1 = JsonDocument.Parse(ExpoChatPushClient.SerializePayload(message with { Badge = 5 }));
        using var v2 = JsonDocument.Parse(ExpoChatPushClient.SerializePayload(invitation with { Badge = 0 }));
        Assert.Equal(5, v1.RootElement.GetProperty("badge").GetInt32());
        Assert.Equal(0, v2.RootElement.GetProperty("badge").GetInt32());
        Assert.False(v2.RootElement.GetProperty("data").TryGetProperty("badge", out _));
    }
}

internal sealed class BadgeSummaryStore : IChatNotificationStore
{
    public int Calls { get; private set; }
    public long UserId { get; private set; }
    public Action? OnRead { get; set; }
    public Exception? Error { get; set; }
    public Task<ChatNotificationSummary> GetSummaryAsync(long userId, CancellationToken token)
    {
        Calls++; UserId = userId; OnRead?.Invoke();
        if (Error is not null) throw Error;
        return Task.FromResult(new ChatNotificationSummary(2, 1, 3));
    }
    public Task<IReadOnlyList<ChatNotificationActivity>> GetActivitiesAsync(long userId, bool onlyUnread, long? cursorId, int limit, CancellationToken token) => throw new NotSupportedException();
    public Task<IReadOnlyList<ChatNotificationChatRow>> GetUnreadChatsAsync(long userId, long? cursorMessageId, int limit, CancellationToken token) => throw new NotSupportedException();
    public Task<ChatNotificationActivity?> MarkReadAsync(long userId, long notificationId, DateTime at, CancellationToken token) => throw new NotSupportedException();
    public Task<long> MarkAllReadAsync(long userId, DateTime at, CancellationToken token) => throw new NotSupportedException();
}
