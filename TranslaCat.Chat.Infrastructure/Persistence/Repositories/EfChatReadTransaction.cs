using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Application.Read;
using TranslaCat.Chat.Domain;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Repositories;

public sealed class EfChatReadTransaction(
    IDbContextFactory<ChatDbContext> contexts,
    IChatReadRecipientResolver recipients,
    IChatReadEventDelivery delivery,
    ILogger<EfChatReadTransaction> logger) : IChatReadTransaction
{
    public async Task<ChatRoomReadResponse> ExecuteAsync(
        Func<IChatReadSession, CancellationToken, Task<ChatRoomReadResponse>> work,
        CancellationToken cancellationToken)
    {
        // 요청마다 독립 context/connection/transaction을 사용한다. 프로세스 내 lock으로 대신하지 않는다.
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var session = new Session(context, recipients);
        var response = await work(session, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);

        // SaveChanges와 outer commit을 구분한다. 이 앞의 실패는 dispose rollback이며 전달은 실행되지 않는다.
        foreach (var intent in session.Intents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (intent)
                {
                    case ChatReadUpdated self:
                        await delivery.DeliverAsync(self, cancellationToken);
                        break;
                    case ChatMemberReadUpdated member:
                        await delivery.DeliverAsync(member, cancellationToken);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 사용자 메시지/recipient/원본 예외 본문은 로그에 남기지 않는다. 독립 이벤트는 계속 시도한다.
                logger.LogWarning("Chat read post-commit delivery failed. Event={EventType}, Failure={FailureType}",
                    intent.GetType().Name, exception.GetType().Name);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }

    private sealed class Session(ChatDbContext context, IChatReadRecipientResolver recipients) : IChatReadSession
    {
        private ChatRoomMemberEntity? lockedMember;
        public List<object> Intents { get; } = [];

        public async Task ValidateOpenRoomMemberAccessAsync(long loginUserId, long chatRoomId, CancellationToken cancellationToken)
        {
            if (!await context.OpenChatRooms.AnyAsync(room => room.ChatRoomId == chatRoomId, cancellationToken))
            {
                return;
            }

            // 읽음에는 원본의 ban→활성 membership→OPEN 유형 검사를 적용한다. CLOSED 검사는 추가하지 않는다.
            if (await context.OpenChatBans.AnyAsync(ban => ban.ChatRoomId == chatRoomId
                    && ban.TargetUserId == loginUserId && ban.ReleasedAt == null, cancellationToken))
            {
                throw new ChatReadException("OPEN_CHAT_BANNED", "해당 OPEN 채팅방에서 차단되어 접근할 수 없습니다.");
            }

            if (!await context.ChatRoomMembers.AnyAsync(member => member.ChatRoomId == chatRoomId
                    && member.UserId == loginUserId && member.Active && member.DeletedAt == null, cancellationToken))
            {
                throw new ChatReadException("OPEN_CHAT_MEMBER_ACCESS_DENIED", "OPEN 채팅방 멤버가 아니거나 접근 권한이 없습니다.");
            }

            if (!await context.ChatRooms.AnyAsync(room => room.Id == chatRoomId && room.RoomType == "OPEN", cancellationToken))
            {
                throw new ChatReadException("OPEN_CHAT_ROOM_NOT_FOUND", "OPEN 채팅방을 찾을 수 없습니다.");
            }
        }

        public async Task<ChatReadMember?> FindActiveMemberForUpdateAsync(long chatRoomId, long loginUserId, CancellationToken cancellationToken)
        {
            // 최신 행을 lock read로 읽어 같은 membership의 cursor 갱신을 commit까지 직렬화한다.
            var members = await context.ChatRoomMembers.FromSqlInterpolated($"SELECT * FROM chat_room_member WHERE chat_room_id = {chatRoomId} AND user_id = {loginUserId} AND active = 1 AND deleted_at IS NULL FOR UPDATE")
                .ToListAsync(cancellationToken);
            lockedMember = members.SingleOrDefault();
            if (lockedMember is null)
            {
                return null;
            }

            var roomType = await context.ChatRooms.Where(room => room.Id == chatRoomId)
                .Select(room => room.RoomType).SingleAsync(cancellationToken);
            var type = roomType switch
            {
                "DIRECT" => ChatReadRoomType.Direct,
                "GROUP" => ChatReadRoomType.Group,
                "OPEN" => ChatReadRoomType.Open,
                _ => throw new InvalidOperationException("지원하지 않는 저장된 room 유형입니다.")
            };
            var username = await recipients.ResolveUsernameAsync(loginUserId, cancellationToken);

            return new ChatReadMember(lockedMember.Id, chatRoomId, loginUserId, username, type,
                lockedMember.JoinedAt, new ReadCursor(lockedMember.LastReadMessageId, lockedMember.LastReadAt));
        }

        public async Task<ChatReadMessage?> FindMessageAsync(long chatRoomId, long messageId, CancellationToken cancellationToken)
        {
            return await context.ChatMessages.AsNoTracking()
                .Where(message => message.Id == messageId && message.ChatRoomId == chatRoomId)
                .Select(message => new ChatReadMessage(message.Id, message.ChatRoomId,
                    message.DeletedAt, message.Status == "SENT", message.CreatedAt))
                .SingleOrDefaultAsync(cancellationToken);
        }

        public async Task SaveAndFlushAsync(ChatReadMember member, ReadCursor cursor, CancellationToken cancellationToken)
        {
            if (lockedMember is null || lockedMember.Id != member.MemberId)
            {
                throw new InvalidOperationException("저장하려는 membership의 lock이 없습니다.");
            }

            // 전진 여부는 실제 P1/P2-A가 결정한다. 여기서 cursor 정책을 다시 구현하지 않는다.
            lockedMember.LastReadMessageId = cursor.LastReadMessageId;
            lockedMember.LastReadAt = cursor.LastReadAt;
            lockedMember.UpdatedAt = cursor.LastReadAt ?? lockedMember.UpdatedAt;
            // BE SecurityUtil.getAuditorIdentity의 최대 50자/안정 ID 대체 규칙을 보존한다.
            lockedMember.UpdatedBy = member.DestinationUsername is { Length: <= 50 } username
                ? username : member.UserId is long userId ? $"USER:{userId}" : lockedMember.UpdatedBy;
            await context.SaveChangesAsync(cancellationToken);
        }

        public async Task<long> CountUnreadAsync(long loginUserId, long chatRoomId, CancellationToken cancellationToken)
        {
            // flush된 같은 transaction을 조회한다. SYSTEM·본인·가입 전·삭제 메시지를 제외한다.
            return await (from member in context.ChatRoomMembers
                          from message in context.ChatMessages
                          where member.ChatRoomId == chatRoomId && member.UserId == loginUserId
                              && member.Active && member.DeletedAt == null
                              && message.ChatRoomId == member.ChatRoomId && message.Status == "SENT"
                              && message.DeletedAt == null && message.MessageType != "SYSTEM"
                              && message.CreatedAt >= member.JoinedAt
                              && (message.SenderType == "AI" || (message.SenderType == "USER"
                                  && message.SenderUserId.HasValue && message.SenderUserId.Value != loginUserId))
                              && (member.LastReadMessageId == null || message.Id > member.LastReadMessageId)
                          select message.Id).LongCountAsync(cancellationToken);
        }

        public void RegisterAfterCommit(ChatReadUpdated readUpdated)
        {
            Intents.Add(readUpdated);
        }

        public void RegisterAfterCommit(ChatMemberReadUpdated memberReadUpdated)
        {
            Intents.Add(memberReadUpdated);
        }
    }
}
