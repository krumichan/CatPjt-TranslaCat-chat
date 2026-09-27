using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Repositories;

public sealed partial class EfChatMessageTransaction(
    IDbContextFactory<ChatDbContext> contexts,
    IChatMessageProfileReader profiles,
    IChatMessageEventDelivery delivery,
    ILogger<EfChatMessageTransaction> logger) : IChatMessageTransaction
{
    public async Task<T> ExecuteAsync<T>(
        Func<IChatMessageSession, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        // 메시지와 번역 pending 행, 응답 조회는 하나의 DB transaction에 속한다.
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        var session = new Session(context, profiles);
        var response = await work(session, cancellationToken);

        if (session.Intents.Count > 0)
        {
            await delivery.ValidateAvailabilityAsync(session.Intents.AsReadOnly(), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);

        // 원본 message.created의 commit 전 전송을 개선한다. 전달 실패는 commit을 되돌리지 않는다.
        foreach (var intent in session.Intents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await delivery.DeliverAsync(intent, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 메시지 본문/프로필/원본 예외는 로그에서 제외하며 독립된 다음 의도를 계속 시도한다.
                logger.LogWarning("Chat message post-commit delivery failed. Event={EventType}, Failure={FailureType}",
                    intent.GetType().Name, exception.GetType().Name);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }

    private sealed partial class Session(ChatDbContext context, IChatMessageProfileReader profiles) : IChatMessageSession
    {
        public List<ChatMessageIntent> Intents { get; } = [];

        public void RegisterAfterCommit(ChatMessageIntent intent)
        {
            Intents.Add(intent);
        }

        public async Task<ChatMessageMember> GetMemberAsync(
            long userId, long roomId, bool forSend, CancellationToken cancellationToken)
        {
            if (forSend)
            {
                // 변경 요청은 최초 snapshot 전에 OPEN lifecycle → room → membership 순서로 잠근다.
                // 종료/차단/탈퇴 commit 뒤 과거 snapshot으로 새 메시지를 쓰는 경합을 막는다.
                await context.OpenChatRooms.FromSqlInterpolated(
                    $"SELECT * FROM open_chat_room WHERE chat_room_id = {roomId} FOR UPDATE").ToListAsync(cancellationToken);
                await context.ChatRooms.FromSqlInterpolated(
                    $"SELECT * FROM chat_room WHERE id = {roomId} FOR UPDATE").ToListAsync(cancellationToken);
                await context.ChatRoomMembers.FromSqlInterpolated(
                    $"SELECT * FROM chat_room_member WHERE chat_room_id = {roomId} AND user_id = {userId} FOR UPDATE").ToListAsync(cancellationToken);
            }

            var openRoom = await context.OpenChatRooms.AsNoTracking()
                .SingleOrDefaultAsync(room => room.ChatRoomId == roomId, cancellationToken);
            if (openRoom is not null)
            {
                await ValidateNotBannedAsync(userId, roomId, cancellationToken);
            }

            var member = await (from membership in context.ChatRoomMembers.AsNoTracking()
                                join room in context.ChatRooms.AsNoTracking() on membership.ChatRoomId equals room.Id
                                where membership.ChatRoomId == roomId && membership.UserId == userId
                                    && membership.Active && membership.DeletedAt == null
                                select new ChatMessageMember(membership.Id, userId, roomId, room.RoomType, membership.JoinedAt))
                .SingleOrDefaultAsync(cancellationToken);
            if (member is null)
            {
                throw new ChatMessageException("채팅방 멤버가 아니거나 접근 권한이 없습니다.",
                    openRoom is null ? "CHAT_ROOM_MEMBER_ACCESS_DENIED" : "OPEN_CHAT_MEMBER_ACCESS_DENIED");
            }

            if (openRoom is not null && member.RoomType != "OPEN")
            {
                throw OpenRoomNotFound();
            }

            // 종료된 OPEN 방의 과거 조회는 허용하고 새 메시지 전송만 원본처럼 거절한다.
            if (forSend && member.RoomType == "OPEN")
            {
                await ValidateNotBannedAsync(userId, roomId, cancellationToken);
                if (openRoom is null)
                {
                    throw OpenRoomNotFound();
                }

                if (openRoom.Status == "CLOSED")
                {
                    throw new ChatMessageException("종료된 OPEN 채팅방에서는 해당 작업을 수행할 수 없습니다.", "OPEN_CHAT_ROOM_CLOSED");
                }
            }

            return member;
        }

        public async Task<IReadOnlyList<ChatStoredMessage>> FetchAsync(
            ChatMessageMember member, long? cursorId, bool forward, int limit, CancellationToken cancellationToken)
        {
            var query = AccessibleMessages(member);
            if (cursorId is not null)
            {
                query = forward
                    ? query.Where(message => message.Id > cursorId.Value)
                    : query.Where(message => message.Id < cursorId.Value);
            }

            query = forward ? query.OrderBy(message => message.Id) : query.OrderByDescending(message => message.Id);
            return await StoredMessages(query.Take(limit)).ToListAsync(cancellationToken);
        }

        public Task<ChatStoredMessage?> FindAccessibleAsync(
            ChatMessageMember member, long messageId, CancellationToken cancellationToken)
        {
            return StoredMessages(AccessibleMessages(member).Where(message => message.Id == messageId))
                .SingleOrDefaultAsync(cancellationToken);
        }

        private IQueryable<ChatMessageEntity> AccessibleMessages(ChatMessageMember member)
        {
            // 재가입 이전 메시지, 삭제 상태 및 soft-delete 행을 pagination의 모든 방향에서 제외한다.
            return context.ChatMessages.AsNoTracking().Where(message => message.ChatRoomId == member.ChatRoomId
                && message.Status == "SENT" && message.DeletedAt == null && message.CreatedAt >= member.JoinedAt);
        }

        private static IQueryable<ChatStoredMessage> StoredMessages(IQueryable<ChatMessageEntity> query)
        {
            return query.Select(message => new ChatStoredMessage(
                message.Id, message.ChatRoomId, message.SenderUserId, message.SenderAiMemberId,
                message.SenderType, message.MessageType, message.Content, message.Status,
                message.CreatedAt, message.UpdatedAt));
        }

        private async Task ValidateNotBannedAsync(long userId, long roomId, CancellationToken cancellationToken)
        {
            if (await context.OpenChatBans.AnyAsync(ban => ban.ChatRoomId == roomId
                    && ban.TargetUserId == userId && ban.ReleasedAt == null, cancellationToken))
            {
                throw new ChatMessageException("해당 OPEN 채팅방에서 차단되어 접근할 수 없습니다.", "OPEN_CHAT_BANNED");
            }
        }

        private static ChatMessageException OpenRoomNotFound()
        {
            return new("OPEN 채팅방을 찾을 수 없습니다.", "OPEN_CHAT_ROOM_NOT_FOUND");
        }
    }
}
