using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Notifications;

namespace TranslaCat.Chat.Infrastructure.Persistence.Notifications;

public sealed class EfChatNotificationStore(IDbContextFactory<ChatDbContext> contexts) : IChatNotificationStore
{
    public async Task<ChatNotificationSummary> GetSummaryAsync(long userId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var unread = VisibleMessages(context, userId).Where(row => row.Unread);
        var messages = await unread.LongCountAsync(cancellationToken);
        var rooms = await unread.Select(row => row.RoomId).Distinct().LongCountAsync(cancellationToken);
        var activities = await context.ChatNotifications.LongCountAsync(row => row.RecipientUserId == userId
            && !row.IsRead && row.DeletedAt == null, cancellationToken);
        return new(messages, rooms, activities);
    }

    public async Task<IReadOnlyList<ChatNotificationActivity>> GetActivitiesAsync(long userId, bool onlyUnread, long? cursorId, int limit, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        return await context.ChatNotifications.AsNoTracking()
            .Where(row => row.RecipientUserId == userId && row.DeletedAt == null
                && (!onlyUnread || !row.IsRead) && (cursorId == null || row.Id < cursorId))
            .OrderByDescending(row => row.Id).Take(limit).Select(row => new ChatNotificationActivity(
                row.Id, row.NotificationType, row.ChatRoomId, row.PayloadJson, row.IsRead, row.ReadAt, row.CreatedAt))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<ChatNotificationActivity?> MarkReadAsync(long userId, long notificationId, DateTime at, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var own = context.ChatNotifications.Where(row => row.Id == notificationId && row.RecipientUserId == userId && row.DeletedAt == null);

        // 조건부 UPDATE로 동시 읽음도 첫 readAt을 보존한다. 이미 읽은 nullable 시각을 채워 넣지 않는다.
        await own.Where(row => !row.IsRead).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.IsRead, true).SetProperty(row => row.ReadAt, at), cancellationToken);
        var result = await own.AsNoTracking().Select(row => new ChatNotificationActivity(
            row.Id, row.NotificationType, row.ChatRoomId, row.PayloadJson, row.IsRead, row.ReadAt, row.CreatedAt))
            .SingleOrDefaultAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<long> MarkAllReadAsync(long userId, DateTime at, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        // 활동 알림만 갱신한다. ChatRoomMember의 메시지 읽음 cursor는 별도 기능이다.
        return await context.ChatNotifications.Where(row => row.RecipientUserId == userId && !row.IsRead && row.DeletedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.IsRead, true).SetProperty(row => row.ReadAt, at), cancellationToken);
    }

    public async Task<IReadOnlyList<ChatNotificationChatRow>> GetUnreadChatsAsync(long userId, long? cursorMessageId, int limit, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        // latest는 자기 메시지도 포함한다. unread만 먼저 필터링하면 알림 정렬과 preview가 달라진다.
        var grouped = VisibleMessages(context, userId).GroupBy(row => new { row.RoomId, row.RoomType, row.SourceType, row.RoomName })
            .Select(group => new
            {
                group.Key.RoomId,
                group.Key.RoomType,
                group.Key.SourceType,
                group.Key.RoomName,
                LatestId = group.Max(row => row.MessageId),
                UnreadCount = group.Sum(row => row.Unread ? 1L : 0L),
                FirstUnreadId = group.Min(row => row.Unread ? row.MessageId : long.MaxValue)
            });
        var page = await grouped.Where(row => row.UnreadCount > 0 && (cursorMessageId == null || row.LatestId < cursorMessageId))
            .OrderByDescending(row => row.LatestId).ThenByDescending(row => row.RoomId).Take(limit).ToArrayAsync(cancellationToken);
        var result = new List<ChatNotificationChatRow>();
        foreach (var row in page)
        {
            var message = await context.ChatMessages.AsNoTracking().SingleOrDefaultAsync(value => value.Id == row.LatestId && value.DeletedAt == null, cancellationToken);
            if (message is null)
            {
                continue;
            }
            var partnerId = row.RoomType == "DIRECT" ? await context.ChatRoomMembers
                .Where(value => value.ChatRoomId == row.RoomId && value.UserId != userId && value.Active && value.DeletedAt == null)
                .Select(value => (long?)value.UserId).FirstOrDefaultAsync(cancellationToken) : null;
            string? aiName = null, openName = null;
            if (message.SenderAiMemberId is { } aiMemberId)
            {
                aiName = await (from member in context.ChatRoomAiMembers
                                join agent in context.ChatAiAgents on member.AiAgentId equals agent.Id
                                where member.Id == aiMemberId
                                select agent.Nickname).FirstOrDefaultAsync(cancellationToken);
            }
            if (row.RoomType == "OPEN" && message.SenderUserId is { } senderId)
            {
                openName = await (from profile in context.OpenChatMemberProfiles
                                  join member in context.ChatRoomMembers on profile.ChatRoomMemberId equals member.Id
                                  where member.ChatRoomId == row.RoomId && member.UserId == senderId
                                  select profile.Nickname).FirstOrDefaultAsync(cancellationToken);
            }
            result.Add(new(row.RoomId, row.RoomType, row.SourceType, row.RoomName, message.Id, message.SenderUserId,
                aiName, openName, message.MessageType, message.Content, message.CreatedAt, partnerId, row.UnreadCount, row.FirstUnreadId));
        }
        return result;
    }

    private static IQueryable<VisibleMessage> VisibleMessages(ChatDbContext context, long userId)
    {
        return from member in context.ChatRoomMembers
               join room in context.ChatRooms on member.ChatRoomId equals room.Id
               join message in context.ChatMessages on room.Id equals message.ChatRoomId
               where member.UserId == userId && member.Active && member.DeletedAt == null
                   && room.Active && room.DeletedAt == null
                   && (room.RoomType != "OPEN" || context.OpenChatRooms.Any(open => open.ChatRoomId == room.Id && open.Status == "ACTIVE"))
                   && message.Status == "SENT" && message.DeletedAt == null && message.MessageType != "SYSTEM"
                   && message.CreatedAt >= member.JoinedAt
               select new VisibleMessage
               {
                   RoomId = room.Id,
                   RoomType = room.RoomType,
                   SourceType = room.SourceType,
                   RoomName = room.Name,
                   MessageId = message.Id,
                   Unread = (member.LastReadMessageId == null || message.Id > member.LastReadMessageId)
                       && (message.SenderType == "AI" || (message.SenderType == "USER" && message.SenderUserId != null && message.SenderUserId != userId))
               };
    }

    private sealed class VisibleMessage
    {
        public long RoomId
        {
            get; init;
        }
        public string RoomType { get; init; } = null!;
        public string SourceType { get; init; } = null!;
        public string? RoomName
        {
            get; init;
        }
        public long MessageId
        {
            get; init;
        }
        public bool Unread
        {
            get; init;
        }
    }
}
