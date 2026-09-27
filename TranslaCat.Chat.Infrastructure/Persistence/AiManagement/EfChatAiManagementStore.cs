using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.OpenMembership;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.AiManagement;

public sealed partial class EfChatAiManagementStore(
    IDbContextFactory<ChatDbContext> contexts,
    IOpenMembershipDelivery events,
    ILogger<EfChatAiManagementStore> logger,
    IChatRoomAccountReader? accounts = null,
    IChatAiProfileStorage? storage = null) : IChatAiManagementStore
{
    public Task<ChatAiMemberList> ListAsync(long userId, long roomId, DateTime now, CancellationToken token)
    {
        return ExecuteAsync(async (db, intents) =>
        {
            await AccessAsync(db, userId, roomId, manage: true, write: false, token);
            var rows = await db.ChatRoomAiMembers.Where(row => row.ChatRoomId == roomId && row.Active && row.DeletedAt == null)
                .OrderBy(row => row.JoinedAt).ToListAsync(token);
            var settings = await EfChatAiSystemSettings.GetOrCreateWithinAsync(db, now, await AuditAsync(userId, token), token);
            var views = new List<ChatAiMember>();
            foreach (var row in rows)
            {
                views.Add(await MapAsync(db, row, token));
            }

            return new ChatAiMemberList(roomId, rows.Count, settings.MaxAiMembersPerRoom, views);
        }, token);
    }

    public Task<ChatAiMember> GetAsync(long userId, long roomId, long memberId, bool safeProfile, CancellationToken token)
    {
        return ExecuteAsync(async (db, intents) =>
        {
            await AccessAsync(db, userId, roomId, manage: !safeProfile, write: false, token);
            var member = await MemberAsync(db, roomId, memberId, token);
            if (safeProfile && !await db.ChatAiAgents.AnyAsync(agent => agent.Id == member.AiAgentId && agent.Active && agent.DeletedAt == null, token))
            {
                throw MemberNotFound();
            }

            return await MapAsync(db, member, token);
        }, token);
    }

    public Task<ChatAiMember> CreateAsync(long userId, long roomId, ChatAiProfileInput? profile, DateTime now, CancellationToken token)
    {
        return ExecuteAsync(async (db, intents) =>
        {
            if (profile is null)
            {
                throw Error("AI 멤버 생성 요청은 필수입니다.", "REQUEST_REQUIRED");
            }

            await AccessAsync(db, userId, roomId, manage: true, write: true, token);
            var audit = await AuditAsync(userId, token);
            var settings = await EfChatAiSystemSettings.GetOrCreateWithinAsync(db, now, audit, token);
            if (await db.ChatRoomAiMembers.CountAsync(row => row.ChatRoomId == roomId && row.Active && row.DeletedAt == null, token) >= settings.MaxAiMembersPerRoom)
            {
                throw Error("채팅방 AI 멤버 최대 인원을 초과했습니다.", "MAX_MEMBER_COUNT_EXCEEDED");
            }

            // AI agent와 방 membership/settings를 하나의 transaction에 저장한다.
            var value = ChatAiProfilePolicy.Normalize(profile);
            var agent = new ChatAiAgentEntity { CreatedAt = now, UpdatedAt = now, CreatedBy = audit, UpdatedBy = audit };
            Apply(agent, value);
            db.ChatAiAgents.Add(agent);
            await db.SaveChangesAsync(token);
            var member = new ChatRoomAiMemberEntity
            {
                ChatRoomId = roomId,
                AiAgentId = agent.Id,
                JoinedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = audit,
                UpdatedBy = audit
            };
            db.ChatRoomAiMembers.Add(member);
            await RoomSettingAsync(db, roomId, now, audit, token);
            await db.SaveChangesAsync(token);
            intents.Add(new OpenMembersChanged(roomId, now));
            return await MapAsync(db, member, token);
        }, token);
    }

    public Task<ChatAiMember> UpdateAsync(long userId, long roomId, long memberId, ChatAiProfileInput? profile, DateTime now, CancellationToken token)
    {
        return ExecuteAsync(async (db, intents) =>
        {
            if (profile is null)
            {
                throw Error("AI 멤버 수정 요청은 필수입니다.", "REQUEST_REQUIRED");
            }

            await AccessAsync(db, userId, roomId, manage: true, write: true, token);
            var member = await MemberAsync(db, roomId, memberId, token);
            var agent = await db.ChatAiAgents.SingleAsync(row => row.Id == member.AiAgentId, token);
            Apply(agent, ChatAiProfilePolicy.Normalize(profile));
            agent.UpdatedAt = now;
            agent.UpdatedBy = await AuditAsync(userId, token);
            await db.SaveChangesAsync(token);
            intents.Add(new OpenMembersChanged(roomId, now));
            return await MapAsync(db, member, token);
        }, token);
    }

    public Task<ChatAiMember> DeleteAsync(long userId, long roomId, long memberId, DateTime now, CancellationToken token)
    {
        return ExecuteAsync(async (db, intents) =>
        {
            await AccessAsync(db, userId, roomId, manage: true, write: true, token);
            var member = await MemberAsync(db, roomId, memberId, token);
            var agent = await db.ChatAiAgents.SingleAsync(row => row.Id == member.AiAgentId, token);
            var audit = await AuditAsync(userId, token);

            // 원본처럼 soft-delete한다. 기존 메시지/agent/object 데이터의 물리 삭제는 하지 않는다.
            member.Active = false;
            member.LeftAt = now;
            member.DeletedAt = now;
            member.UpdatedAt = now;
            member.UpdatedBy = audit;
            agent.Active = false;
            agent.DeletedAt = now;
            agent.UpdatedAt = now;
            agent.UpdatedBy = audit;
            await db.SaveChangesAsync(token);
            intents.Add(new OpenMembersChanged(roomId, now));
            return await MapAsync(db, member, token);
        }, token);
    }

    private async Task<T> ExecuteAsync<T>(Func<ChatDbContext, List<OpenMembershipIntent>, Task<T>> work, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var intents = new List<OpenMembershipIntent>();
        var result = await work(db, intents);
        await events.ValidateAvailabilityAsync(intents, token);
        await transaction.CommitAsync(token);

        // 전송 실패는 commit된 설정/멤버를 되돌리지 않는다. 로그에는 persona나 사용자 내용을 남기지 않는다.
        foreach (var intent in intents)
        {
            try
            {
                await events.DeliverAsync(intent, CancellationToken.None);
            }
            catch (Exception exception) { logger.LogWarning("AI management event failed ({FailureType}).", exception.GetType().Name); }
        }
        return result;
    }

    private static async Task AccessAsync(ChatDbContext db, long userId, long roomId, bool manage, bool write, CancellationToken token)
    {
        // OPEN lifecycle과 같은 잠금 순서를 사용해 종료/탈퇴와 AI 관리의 경합을 제한한다.
        if (write)
        {
            await db.OpenChatRooms.FromSqlInterpolated($"SELECT * FROM open_chat_room WHERE chat_room_id = {roomId} FOR UPDATE").ToListAsync(token);
        }

        var room = write
            ? (await db.ChatRooms.FromSqlInterpolated($"SELECT * FROM chat_room WHERE id = {roomId} AND active = 1 AND deleted_at IS NULL FOR UPDATE").ToListAsync(token)).SingleOrDefault()
            : await db.ChatRooms.SingleOrDefaultAsync(row => row.Id == roomId && row.Active && row.DeletedAt == null, token);
        if (room is null)
        {
            throw Error("활성 채팅방을 찾을 수 없습니다.", "ROOM_NOT_FOUND");
        }

        if (room.RoomType is not ("GROUP" or "OPEN"))
        {
            throw Error("AI 멤버는 GROUP 또는 OPEN 채팅방에서만 사용할 수 있습니다.", "ROOM_TYPE_NOT_SUPPORTED");
        }

        var member = write
            ? (await db.ChatRoomMembers.FromSqlInterpolated($"SELECT * FROM chat_room_member WHERE chat_room_id = {roomId} AND user_id = {userId} AND active = 1 AND deleted_at IS NULL FOR UPDATE").ToListAsync(token)).SingleOrDefault()
            : await db.ChatRoomMembers.SingleOrDefaultAsync(row => row.ChatRoomId == roomId && row.UserId == userId && row.Active && row.DeletedAt == null, token);
        if (member is null)
        {
            throw Error(manage && write ? "채팅방 OWNER 또는 ADMIN만 AI 멤버를 관리할 수 있습니다." : "채팅방 멤버가 아니거나 AI 설정 조회 권한이 없습니다.",
                manage && write ? "ROOM_MANAGEMENT_ACCESS_DENIED" : "ROOM_MEMBER_ACCESS_DENIED");
        }

        if (manage && member.Role is not ("OWNER" or "ADMIN"))
        {
            throw Error("채팅방 OWNER 또는 ADMIN만 AI 멤버를 관리할 수 있습니다.", "ROOM_MANAGEMENT_ACCESS_DENIED");
        }

        // 차단은 모든 접근에 적용한다. 종료방의 기록 조회는 보존하되 AI 관리 변경은 거절한다.
        if (room.RoomType == "OPEN")
        {
            if (await db.OpenChatBans.AnyAsync(row => row.ChatRoomId == roomId && row.TargetUserId == userId && row.ReleasedAt == null, token))
            {
                throw new ChatAiManagementException("차단된 OPEN 채팅방에는 접근할 수 없습니다.", "OPEN_CHAT_BANNED");
            }

            if (write && manage && await db.OpenChatRooms.AnyAsync(row => row.ChatRoomId == roomId && row.Status == "CLOSED", token))
            {
                throw new ChatAiManagementException("종료된 OPEN 채팅방에서는 해당 작업을 수행할 수 없습니다.", "OPEN_CHAT_ROOM_CLOSED");
            }
        }
    }

    private static async Task<ChatRoomAiMemberEntity> MemberAsync(ChatDbContext db, long roomId, long memberId, CancellationToken token)
    {
        return await db.ChatRoomAiMembers.SingleOrDefaultAsync(row => row.Id == memberId && row.ChatRoomId == roomId && row.Active && row.DeletedAt == null, token)
        ?? throw MemberNotFound();
    }

    private async Task<ChatAiMember> MapAsync(ChatDbContext db, ChatRoomAiMemberEntity member, CancellationToken token)
    {
        var agent = await db.ChatAiAgents.SingleAsync(row => row.Id == member.AiAgentId, token);
        return new(member.Id, agent.Id, member.ChatRoomId, agent.Nickname, await UrlAsync(agent.ProfileImageObjectKey, token),
            await UrlAsync(agent.ProfileBackgroundImageObjectKey, token), agent.Bio, agent.OriginalLanguageCode, agent.PersonaPrompt,
            member.Active, member.JoinedAt, member.CreatedAt, member.UpdatedAt);
    }

    private Task<string?> UrlAsync(string? key, CancellationToken token)
    {
        return string.IsNullOrWhiteSpace(key) ? Task.FromResult<string?>(null)
        : (storage ?? throw new ChatAiManagementUnavailableException()).ResolveUrlAsync(key, token);
    }

    private async Task<string> AuditAsync(long userId, CancellationToken token)
    {
        var audit = await (accounts ?? throw new ChatAiManagementUnavailableException()).GetAuditIdentityAsync(userId, token);
        return audit is { Length: <= 50 } ? audit : throw new ChatAiManagementUnavailableException();
    }

    private static void Apply(ChatAiAgentEntity agent, ChatAiProfile value)
    {
        agent.Nickname = value.Nickname;
        agent.Bio = value.Bio;
        agent.OriginalLanguageCode = value.OriginalLanguageCode;
        agent.PersonaPrompt = value.PersonaPrompt;
    }

    private static ChatAiManagementException Error(string message, string code)
    {
        return ChatAiProfilePolicy.Error(message, code);
    }

    private static ChatAiManagementException MemberNotFound()
    {
        return Error("활성 AI 멤버를 찾을 수 없습니다.", "MEMBER_NOT_FOUND");
    }
}
