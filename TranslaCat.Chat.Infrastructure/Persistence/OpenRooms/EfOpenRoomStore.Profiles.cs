using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.OpenRooms;

public sealed partial class EfOpenRoomStore
{
    public async Task<OpenProfile> GetProfileAsync(long userId, long roomId, long? memberId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var actor = await ActiveMemberAsync(context, userId, roomId, cancellationToken);
        var row = memberId is null
            ? await MyProfileAsync(context, actor.Id, cancellationToken)
            : await Profiles(context).SingleOrDefaultAsync(row => row.Member.Id == memberId
                && row.Member.ChatRoomId == roomId && row.Member.Active && row.Member.DeletedAt == null, cancellationToken)
                ?? throw OpenRoomPolicy.Error("OPEN 채팅 멤버 프로필을 찾을 수 없습니다.", "MEMBER_NOT_FOUND");
        var visible = await DisclosureAsync(context, roomId, cancellationToken) != "PRIVATE";
        var online = visible && presence is not null
            ? await presence.ResolveOnlineAsync(row.Member.UserId, cancellationToken) : null;
        return await MapProfileAsync(row, online, cancellationToken);
    }

    public async Task<OpenMemberList> GetMembersAsync(long userId, long roomId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await ActiveMemberAsync(context, userId, roomId, cancellationToken);
        var rows = await Profiles(context).Where(row => row.Member.ChatRoomId == roomId
            && row.Member.Active && row.Member.DeletedAt == null).OrderBy(row => row.Member.JoinedAt).ToListAsync(cancellationToken);
        var disclosure = await DisclosureAsync(context, roomId, cancellationToken);
        var online = disclosure != "PRIVATE" && presence is not null
            ? await presence.ResolveOnlineByUserIdsAsync(rows.Select(row => row.Member.UserId), cancellationToken)
            : new Dictionary<long, bool>();
        var members = new List<OpenProfile>();
        foreach (var row in rows)
        {
            members.Add(await MapProfileAsync(row, online.TryGetValue(row.Member.UserId, out var value) ? value : null, cancellationToken));
        }

        // PRIVATE도 원본의 안전한 AI 표시 DTO는 반환한다. persona/prompt/전역 user 정보는 노출하지 않는다.
        var aiRows = await (from member in context.ChatRoomAiMembers
                            join agent in context.ChatAiAgents on member.AiAgentId equals agent.Id
                            where member.ChatRoomId == roomId && member.Active && member.DeletedAt == null
                                && agent.Active && agent.DeletedAt == null
                            orderby member.JoinedAt
                            select new
                            {
                                Member = member,
                                Agent = agent
                            }).ToListAsync(cancellationToken);
        var aiMembers = new List<OpenAiDisplayMember>();
        foreach (var row in aiRows)
        {
            aiMembers.Add(new(row.Member.Id, row.Agent.Nickname, await UrlAsync(row.Agent.ProfileImageObjectKey, cancellationToken),
                "MEMBER", row.Member.Active, row.Member.JoinedAt));
        }
        return new(members.AsReadOnly(), aiMembers.AsReadOnly(), aiMembers.Count == 0 ? null : disclosure);
    }

    public Task<OpenProfile> UpdateNicknameAsync(long userId, long roomId, string? nickname, DateTime now, CancellationToken cancellationToken)
    {
        return ChangeProfileAsync(userId, roomId, nickname, false, now, cancellationToken);
    }

    public Task<OpenProfile> DeleteImageAsync(long userId, long roomId, DateTime now, CancellationToken cancellationToken)
    {
        return ChangeProfileAsync(userId, roomId, null, true, now, cancellationToken);
    }

    private async Task<OpenProfile> ChangeProfileAsync(long userId, long roomId, string? nickname,
        bool deleteImage, DateTime now, CancellationToken token)
    {
        await using var context = await contexts.CreateDbContextAsync(token);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);

        // 첫 snapshot 조회 전에 방 lock을 얻어 종료/탈퇴 직후의 과거 membership을 읽지 않는다.
        // lock 조회 자체는 오류를 먼저 반환하지 않아 ban→membership→방 상태의 원본 판정 순서를 보존한다.
        var locked = await context.OpenChatRooms.FromSqlInterpolated($"SELECT o.* FROM open_chat_room o JOIN chat_room r ON r.id = o.chat_room_id WHERE o.chat_room_id = {roomId} AND r.active = 1 AND r.deleted_at IS NULL FOR UPDATE")
            .ToListAsync(token);
        var member = await ActiveMemberAsync(context, userId, roomId, token);
        var open = locked.SingleOrDefault() ?? throw OpenRoomPolicy.Error("OPEN 채팅방을 찾을 수 없습니다.", "ROOM_NOT_FOUND");
        if (open.Status == "CLOSED")
        {
            throw OpenRoomPolicy.Error("종료된 OPEN 채팅방에서는 해당 작업을 수행할 수 없습니다.", "ROOM_CLOSED");
        }
        var row = await MyProfileAsync(context, member.Id, token);
        string? normalized = deleteImage ? null : OpenRoomPolicy.NormalizeNickname(nickname);
        if (events?.IsConfigured != true)
        {
            throw new OpenRoomDependencyUnavailableException();
        }
        var auditor = await AuditAsync(userId, token);
        string? oldKey = deleteImage ? row.Profile.ProfileImageObjectKey : null;
        if (!ChatMessageText.IsBlank(oldKey) && storage is null)
        {
            throw new OpenRoomDependencyUnavailableException();
        }

        if (deleteImage)
        {
            row.Profile.ProfileImageObjectKey = null;
        }
        else
        {
            row.Profile.Nickname = normalized!;
        }
        row.Profile.UpdatedAt = now;
        row.Profile.UpdatedBy = auditor;
        await context.SaveChangesAsync(token);
        var response = await MapProfileAsync(row, null, token);
        var change = new OpenProfileChanged(roomId, member.Id, row.Profile.MemberCode,
            response.Nickname, response.ProfileImageUrl, member.Role, now);
        await transaction.CommitAsync(token);

        // 기존 object 삭제와 이벤트는 commit 이후에만 실행한다. 실패가 DB rollback을 의미하지 않는다.
        if (!ChatMessageText.IsBlank(oldKey))
        {
            try
            {
                await storage!.DeleteAsync(oldKey!, token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("OPEN profile image cleanup failed ({FailureType}).", exception.GetType().Name);
            }
        }
        try
        {
            await events.DeliverAsync(change, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("OPEN profile event delivery failed ({FailureType}).", exception.GetType().Name);
        }
        return response;
    }

    private static async Task<ChatRoomMemberEntity> ActiveMemberAsync(ChatDbContext context, long userId, long roomId, CancellationToken token)
    {
        if (await context.OpenChatBans.AnyAsync(ban => ban.ChatRoomId == roomId
            && ban.TargetUserId == userId && ban.ReleasedAt == null, token))
        {
            throw OpenRoomPolicy.Error("해당 OPEN 채팅방에서 차단되어 접근할 수 없습니다.", "BANNED");
        }
        var row = await (from member in context.ChatRoomMembers
                         join room in context.ChatRooms on member.ChatRoomId equals room.Id
                         where member.ChatRoomId == roomId && member.UserId == userId && member.Active && member.DeletedAt == null
                         select new
                         {
                             Member = member,
                             room.RoomType
                         }).SingleOrDefaultAsync(token)
            ?? throw OpenRoomPolicy.Error("OPEN 채팅방 멤버가 아니거나 접근 권한이 없습니다.", "MEMBER_ACCESS_DENIED");
        if (row.RoomType != "OPEN")
        {
            throw OpenRoomPolicy.Error("OPEN 채팅방을 찾을 수 없습니다.", "ROOM_NOT_FOUND");
        }
        return row.Member;
    }

    private static async Task<ProfileRow> MyProfileAsync(ChatDbContext context, long memberId, CancellationToken token)
    {
        return await Profiles(context).SingleOrDefaultAsync(row => row.Member.Id == memberId, token)
        ?? throw OpenRoomPolicy.Error("OPEN 채팅 프로필을 찾을 수 없습니다.", "PROFILE_NOT_FOUND");
    }
}
