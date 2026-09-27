using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TranslaCat.Chat.Application.Language;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Membership;

public sealed class EfChatMembershipStore(
    IDbContextFactory<ChatDbContext> contexts,
    IChatMembershipEventDelivery? delivery,
    ILogger<EfChatMembershipStore>? logger = null) : IChatMembershipStore
{
    public async Task<T> ExecuteAsync<T>(Func<IChatMembershipSession, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var session = new Session(context);
        var result = await work(session, cancellationToken);
        if (session.Intents.Count > 0)
        {
            if (delivery is null)
            {
                throw new ChatMembershipDependencyUnavailableException();
            }
            await delivery.ValidateAvailabilityAsync(session.Intents, cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // 상태/메시지 flush는 commit이 아니다. 방 변경과 알림 intent는 실제 commit 성공 이후에만 전달한다.
        foreach (var intent in session.Intents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await delivery!.DeliverAsync(intent, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 원본 AFTER_COMMIT listener처럼 이미 저장된 요청을 실패로 바꾸지 않고 다음 의도를 시도한다.
                // 계정/이메일/메시지 및 원본 예외 내용은 기록하지 않는다. 재시도·내구성 보장은 별도다.
                (logger ?? NullLogger<EfChatMembershipStore>.Instance).LogWarning(
                    "Chat membership post-commit delivery failed. Event={EventType}, Failure={FailureType}",
                    intent.GetType().Name, exception.GetType().Name);
            }
        }
        return result;
    }

    private sealed class Session(ChatDbContext context) : IChatMembershipSession
    {
        public List<ChatMembershipIntent> Intents { get; } = [];

        public async Task<ChatMembershipRoom> LockRoomAsync(long roomId, CancellationToken cancellationToken)
        {
            var rooms = await context.ChatRooms.FromSqlInterpolated(
                $"SELECT * FROM chat_room WHERE id = {roomId} AND active = 1 AND deleted_at IS NULL FOR UPDATE")
                .ToArrayAsync(cancellationToken);
            var room = rooms.SingleOrDefault() ?? throw new ChatMembershipException("채팅방을 찾을 수 없습니다.", "CHAT_ROOM_NOT_FOUND");
            return new(room.Id, room.RoomType, room.SourceType, room.Name);
        }

        public async Task<IReadOnlyList<ChatMembershipMember>> GetActiveMembersAsync(long roomId, CancellationToken cancellationToken)
        {
            return await context.ChatRoomMembers.Where(member => member.ChatRoomId == roomId && member.Active && member.DeletedAt == null)
                        .OrderBy(member => member.Id).Select(member => new ChatMembershipMember(member.Id, member.UserId, member.Role, member.JoinedAt))
                        .ToArrayAsync(cancellationToken);
        }

        public Task<long?> GetLatestSentMessageIdAsync(long roomId, CancellationToken cancellationToken)
        {
            return context.ChatMessages.Where(message => message.ChatRoomId == roomId && message.Status == "SENT" && message.DeletedAt == null)
                        .OrderByDescending(message => message.Id).Select(message => (long?)message.Id).FirstOrDefaultAsync(cancellationToken);
        }

        public Task<long?> FindFriendDirectAsync(long userId, long friendUserId, CancellationToken cancellationToken)
        {
            return context.ChatRooms.Where(room => room.RoomType == "DIRECT" && room.SourceType == "FRIEND"
                            && room.Active && room.DeletedAt == null
                            && context.ChatRoomMembers.Any(member => member.ChatRoomId == room.Id && member.UserId == userId && member.Active && member.DeletedAt == null)
                            && context.ChatRoomMembers.Any(member => member.ChatRoomId == room.Id && member.UserId == friendUserId && member.Active && member.DeletedAt == null)
                            && context.ChatRoomMembers.Count(member => member.ChatRoomId == room.Id && member.Active && member.DeletedAt == null) == 2)
                        .OrderBy(room => room.Id).Select(room => (long?)room.Id).FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<ChatMembershipRoom> CreateFriendDirectAsync(ChatMembershipUser owner, DateTime now, CancellationToken cancellationToken)
        {
            var room = new ChatRoomEntity
            {
                RoomType = "DIRECT",
                SourceType = "FRIEND",
                OwnerId = owner.Id,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = owner.Email,
                UpdatedBy = owner.Email
            };
            context.ChatRooms.Add(room);
            await context.SaveChangesAsync(cancellationToken);
            return new(room.Id, room.RoomType, room.SourceType, room.Name);
        }

        public async Task<ChatMembershipRoom> CreateGroupAsync(string? name, string? description, ChatMembershipUser owner, string sourceType, DateTime now, CancellationToken cancellationToken)
        {
            var room = new ChatRoomEntity
            {
                RoomType = "GROUP",
                SourceType = sourceType,
                Name = name,
                Description = description,
                OwnerId = owner.Id,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = owner.Email,
                UpdatedBy = owner.Email
            };
            context.ChatRooms.Add(room);
            await context.SaveChangesAsync(cancellationToken);
            return new(room.Id, room.RoomType, room.SourceType, room.Name);
        }

        public async Task<ChatMembershipMember> AddOrRestoreAsync(ChatMembershipRoom room, ChatMembershipUser user, string role,
            long? initialCursor, string auditIdentity, DateTime now, CancellationToken cancellationToken)
        {
            var language = await context.UserChatLanguageSettings.Where(setting => setting.UserId == user.Id)
                .Select(setting => new ChatLanguageValues(setting.OriginalLanguageCode, setting.TranslationLanguageCode,
                    setting.ShowOriginal, setting.ShowTranslation)).SingleOrDefaultAsync(cancellationToken) ?? ChatLanguageService.SystemDefault;
            var member = await context.ChatRoomMembers.SingleOrDefaultAsync(value => value.ChatRoomId == room.Id && value.UserId == user.Id, cancellationToken);
            if (member is null)
            {
                member = new ChatRoomMemberEntity { ChatRoomId = room.Id, UserId = user.Id, CreatedAt = now, CreatedBy = auditIdentity };
                context.ChatRoomMembers.Add(member);
            }

            // 재초대는 과거 role/읽음/퇴장 상태를 초기화하고 현재 개인 기본 언어 snapshot을 적용한다.
            member.Role = role;
            member.OriginalLanguageCode = language.OriginalLanguageCode;
            member.TranslationLanguageCode = language.TranslationLanguageCode;
            member.ShowOriginal = language.ShowOriginal;
            member.ShowTranslation = language.ShowTranslation;
            member.Active = true;
            member.JoinedAt = now;
            member.LeftAt = null;
            member.DeletedAt = null;
            member.LastReadMessageId = initialCursor;
            member.LastReadAt = initialCursor.HasValue ? now : null;
            member.UpdatedBy = auditIdentity;
            member.UpdatedAt = now;
            await context.SaveChangesAsync(cancellationToken);
            return new(member.Id, member.UserId, member.Role, member.JoinedAt);
        }

        public async Task<ChatMessageView> InsertSystemMessageAsync(long roomId, string content, string auditIdentity, DateTime now, CancellationToken cancellationToken)
        {
            var message = new ChatMessageEntity
            {
                ChatRoomId = roomId,
                SenderType = "SYSTEM",
                MessageType = "SYSTEM",
                Content = content,
                Status = "SENT",
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = auditIdentity,
                UpdatedBy = auditIdentity
            };
            context.ChatMessages.Add(message);
            await context.SaveChangesAsync(cancellationToken);
            return new(message.Id, roomId, null, null, null, null, null, "SYSTEM", "SYSTEM", content, "SENT", null, [], now, now, null);
        }

        public void RegisterAfterCommit(ChatMembershipIntent intent)
        {
            Intents.Add(intent);
        }
    }
}
