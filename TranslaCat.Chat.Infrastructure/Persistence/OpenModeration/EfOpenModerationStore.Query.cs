using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.OpenModeration;
using TranslaCat.Chat.Application.OpenRooms;

namespace TranslaCat.Chat.Infrastructure.Persistence.OpenModeration;

public sealed partial class EfOpenModerationStore
{
    public async Task<OpenBanList> ListAsync(long actorId, long roomId, string? keyword, long? cursorId, int? size, CancellationToken token)
    {
        await using var context = await contexts.CreateDbContextAsync(token);

        // 목록은 CLOSED에서도 허용한다. 원본처럼 ban→활성 OPEN membership→운영 권한 순으로 검사한다.
        if (await context.OpenChatBans.AnyAsync(ban => ban.ChatRoomId == roomId && ban.TargetUserId == actorId && ban.ReleasedAt == null, token))
        {
            throw OpenRoomPolicy.Error("해당 OPEN 채팅방에서 차단되어 접근할 수 없습니다.", "BANNED");
        }

        var actor = await (from member in context.ChatRoomMembers
                           join room in context.ChatRooms on member.ChatRoomId equals room.Id
                           where member.ChatRoomId == roomId && member.UserId == actorId && member.Active && member.DeletedAt == null
                           select new
                           {
                               Member = member,
                               room.RoomType
                           }).SingleOrDefaultAsync(token)
            ?? throw OpenRoomPolicy.Error("OPEN 채팅방 멤버가 아니거나 접근 권한이 없습니다.", "MEMBER_ACCESS_DENIED");
        if (actor.RoomType != "OPEN")
        {
            throw OpenRoomPolicy.Error("OPEN 채팅방을 찾을 수 없습니다.", "ROOM_NOT_FOUND");
        }
        if (actor.Member.Role is not ("OWNER" or "ADMIN"))
        {
            throw OpenRoomPolicy.Error("OPEN 채팅방 OWNER 또는 ADMIN만 블랙리스트를 조회할 수 있습니다.", "MODERATION_ACCESS_DENIED");
        }

        // 입력 검사는 접근 검증 뒤에 수행하여 원본 오류 우선순위를 보존한다.
        string? search = ChatMessageText.IsBlank(keyword) ? null : ChatMessageText.Trim(keyword!);
        if (search?.Length > 100)
        {
            throw OpenRoomPolicy.Error("OPEN 블랙리스트 검색어는 100자 이하여야 합니다.", "KEYWORD_TOO_LONG");
        }
        if (cursorId is <= 0)
        {
            throw OpenRoomPolicy.Error("cursor는 1 이상이어야 합니다.", "CURSOR_INVALID");
        }
        int requested = size ?? 20;
        if (requested is <= 0 or > 50)
        {
            throw OpenRoomPolicy.Error("size는 1 이상 50 이하여야 합니다.", "PAGE_SIZE_INVALID");
        }

        // 대상 닉네임/프로필은 ban 시점 snapshot, actor 닉네임은 현재 방별 프로필이다.
        var query = from ban in context.OpenChatBans
                    join member in context.ChatRoomMembers on ban.BannedByMemberId equals member.Id
                    join profile in context.OpenChatMemberProfiles on member.Id equals profile.ChatRoomMemberId
                    where ban.ChatRoomId == roomId && ban.ReleasedAt == null
                    select new
                    {
                        Ban = ban,
                        Actor = member,
                        Profile = profile
                    };
        if (cursorId is not null)
        {
            query = query.Where(row => row.Ban.Id < cursorId.Value);
        }
        if (search is not null)
        {
            string lowered = search.ToLowerInvariant();
            query = query.Where(row => row.Ban.NicknameSnapshot.ToLower().Contains(lowered)
                || row.Ban.TargetMemberCode.ToLower().Contains(lowered));
        }

        var fetched = await query.OrderByDescending(row => row.Ban.Id).Take(requested + 1).ToListAsync(token);
        var items = new List<OpenBanItem>();
        foreach (var row in fetched.Take(requested))
        {
            items.Add(new(row.Ban.Id, row.Ban.TargetChatRoomMemberId, row.Ban.TargetMemberCode, row.Ban.NicknameSnapshot,
                await UrlAsync(row.Ban.ProfileImageObjectKeySnapshot, token), row.Ban.LastJoinedAtSnapshot, row.Ban.BannedAt,
                new(row.Actor.Id, row.Profile.Nickname, row.Ban.BannedByRole), row.Ban.Reason,
                OpenModerationPolicy.CanRelease(actor.Member.Role, row.Ban.BannedByRole, row.Ban.TargetRoleSnapshot)));
        }

        bool hasNext = fetched.Count > requested;
        return new(items.AsReadOnly(), hasNext ? items[^1].BanId : null, hasNext);
    }
}
