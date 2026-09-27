namespace TranslaCat.Chat.Application.Notifications;

public sealed record ChatNotificationActivity(long Id, string NotificationType, long? RoomId, string PayloadJson, bool IsRead, DateTime? ReadAt, DateTime CreatedAt);
public sealed record ChatNotificationActivityPage(IReadOnlyList<ChatNotificationActivity> Items, long? NextCursorId, bool HasNext);
public sealed record ChatNotificationSummary(long UnreadChatMessageCount, long UnreadChatRoomCount, long UnreadActivityCount)
{
    public long TotalAttentionCount => UnreadChatMessageCount + UnreadActivityCount;
}
public sealed record ChatNotificationChatRow(long RoomId, string RoomType, string SourceType, string? RoomName,
    long LatestMessageId, long? LatestSenderUserId, string? AiSenderName, string? OpenSenderName,
    string MessageType, string Content, DateTime CreatedAt, long? PartnerUserId, long UnreadCount, long FirstUnreadMessageId);
public sealed record ChatNotificationLatestMessage(long Id, string? SenderDisplayName, string MessageType, string? ContentPreview, DateTime CreatedAt);
public sealed record ChatNotificationChatItem(long RoomId, string RoomType, string SourceType, string? RoomDisplayName,
    string? RoomAvatarUrl, ChatNotificationLatestMessage LatestMessage, long UnreadCount, long FirstUnreadMessageId);
public sealed record ChatNotificationChatPage(IReadOnlyList<ChatNotificationChatItem> Items, long? NextCursorMessageId, bool HasNext);
public sealed record ChatNotificationUserDisplay(string? DisplayName, string? AvatarUrl);

public interface IChatNotificationProfileReader
{
    // 현재 일반 profile.nickname 및 profile URL을 사용하고, profile 부재 시 username/publicId를 적용한다.
    Task<ChatNotificationUserDisplay> GetDisplayAsync(long userId, CancellationToken cancellationToken);
}

public interface IChatNotificationStore
{
    Task<ChatNotificationSummary> GetSummaryAsync(long userId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatNotificationActivity>> GetActivitiesAsync(long userId, bool onlyUnread, long? cursorId, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatNotificationChatRow>> GetUnreadChatsAsync(long userId, long? cursorMessageId, int limit, CancellationToken cancellationToken);
    Task<ChatNotificationActivity?> MarkReadAsync(long userId, long notificationId, DateTime at, CancellationToken cancellationToken);
    Task<long> MarkAllReadAsync(long userId, DateTime at, CancellationToken cancellationToken);
}

public sealed class ChatNotificationException(string message, string code = "") : Exception(message)
{
    public string Code { get; } = code;
}
public sealed class ChatNotificationDependencyUnavailableException : Exception;

public sealed class ChatNotificationService(IChatNotificationStore store, IChatNotificationProfileReader? profiles, Func<DateTime> clock)
{
    public Task<ChatNotificationSummary> GetSummaryAsync(long userId, CancellationToken cancellationToken = default)
    {
        return store.GetSummaryAsync(userId, cancellationToken);
    }

    public async Task<ChatNotificationActivityPage> GetActivitiesAsync(long userId, bool onlyUnread = false,
        long? cursorId = null, int? size = null, CancellationToken cancellationToken = default)
    {
        if (cursorId is <= 0)
        {
            throw new ChatNotificationException("cursorId는 1 이상이어야 합니다.", "CHAT_NOTIFICATION_CURSOR_INVALID");
        }
        var pageSize = PageSize(size);
        var fetched = await store.GetActivitiesAsync(userId, onlyUnread, cursorId, pageSize + 1, cancellationToken);
        var hasNext = fetched.Count > pageSize;
        var page = fetched.Take(pageSize).ToArray();
        return new(page, hasNext && page.Length > 0 ? page[^1].Id : null, hasNext);
    }

    public async Task<ChatNotificationChatPage> GetUnreadChatsAsync(long userId, long? cursorMessageId = null,
        int? size = null, CancellationToken cancellationToken = default)
    {
        if (cursorMessageId is <= 0)
        {
            throw new ChatNotificationException("cursorMessageId는 1 이상이어야 합니다.");
        }
        var pageSize = PageSize(size);
        var fetched = await store.GetUnreadChatsAsync(userId, cursorMessageId, pageSize + 1, cancellationToken);
        var hasNext = fetched.Count > pageSize;
        var page = fetched.Take(pageSize).ToArray();
        var items = new List<ChatNotificationChatItem>();

        // OPEN 이름은 방 전용 프로필에서만 가져온다. 일반 계정 이름으로 익명성을 깨지 않는다.
        foreach (var row in page)
        {
            var roomName = row.RoomName;
            string? avatar = null;
            if (row.RoomType == "DIRECT" && row.PartnerUserId is { } partnerId)
            {
                var display = await DisplayAsync(partnerId, cancellationToken);
                roomName = display.DisplayName;
                avatar = display.AvatarUrl;
            }
            var senderName = row.AiSenderName;
            if (senderName is null && row.LatestSenderUserId is { } senderId)
            {
                senderName = row.RoomType == "OPEN" ? row.OpenSenderName
                    : (await DisplayAsync(senderId, cancellationToken)).DisplayName;
            }
            items.Add(new(row.RoomId, row.RoomType, row.SourceType, roomName, avatar,
                new(row.LatestMessageId, senderName, row.MessageType, Preview(row.Content), row.CreatedAt),
                row.UnreadCount, row.FirstUnreadMessageId));
        }
        return new(items, hasNext && page.Length > 0 ? page[^1].LatestMessageId : null, hasNext);
    }

    public async Task<ChatNotificationActivity> MarkReadAsync(long userId, long notificationId, CancellationToken cancellationToken = default)
    {
        if (notificationId <= 0)
        {
            throw new ChatNotificationException("notificationId는 1 이상이어야 합니다.", "CHAT_NOTIFICATION_NOT_FOUND");
        }
        return await store.MarkReadAsync(userId, notificationId, clock(), cancellationToken)
            ?? throw new ChatNotificationException("활동 알림을 찾을 수 없습니다.", "CHAT_NOTIFICATION_NOT_FOUND");
    }

    public Task<long> MarkAllReadAsync(long userId, CancellationToken cancellationToken = default)
    {
        return store.MarkAllReadAsync(userId, clock(), cancellationToken);
    }

    private async Task<ChatNotificationUserDisplay> DisplayAsync(long userId, CancellationToken cancellationToken)
    {
        if (profiles is null)
        {
            throw new ChatNotificationDependencyUnavailableException();
        }
        return await profiles.GetDisplayAsync(userId, cancellationToken);
    }

    private static int PageSize(int? requested)
    {
        if (requested is <= 0)
        {
            throw new ChatNotificationException("size는 1 이상이어야 합니다.");
        }
        return Math.Min(requested ?? 20, 50);
    }

    private static string Preview(string content)
    {
        var normalized = content.Replace('\r', ' ').Replace('\n', ' ');
        int first = 0, last = normalized.Length - 1;
        while (first <= last && normalized[first] <= '\u0020')
        {
            first++;
        }
        while (last >= first && normalized[last] <= '\u0020')
        {
            last--;
        }
        normalized = normalized[first..(last + 1)];
        return normalized.Length <= 160 ? normalized : normalized[..160];
    }
}
