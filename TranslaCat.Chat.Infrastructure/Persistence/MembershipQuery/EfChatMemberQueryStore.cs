using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.MembershipQuery;

namespace TranslaCat.Chat.Infrastructure.Persistence.MembershipQuery;

public sealed class EfChatMemberQueryStore(IDbContextFactory<ChatDbContext> contexts, IChatAiProfileStorage? storage) : IChatMemberQueryStore
{
    public async Task<ChatMemberQueryState> ReadAsync(long userId, long roomId, long? targetUserId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        // 원본 member query는 활성 membership을 검증한다. 방 active/deleted 조건을 새로 추가하지 않는다.
        var roomType = await (from member in context.ChatRoomMembers
                              join room in context.ChatRooms on member.ChatRoomId equals room.Id
                              where member.ChatRoomId == roomId && member.UserId == userId && member.Active && member.DeletedAt == null
                              select room.RoomType).SingleOrDefaultAsync(cancellationToken) ?? throw AccessDenied();
        if (roomType == "OPEN")
        {
            throw new ChatMembershipException("OPEN 채팅방에서는 OPEN 전용 멤버 프로필 API를 사용해야 합니다.", "OPEN_CHAT_MEMBER_PROFILE_API_REQUIRED");
        }
        var membersQuery = context.ChatRoomMembers.Where(member => member.ChatRoomId == roomId && member.Active && member.DeletedAt == null);
        if (targetUserId is not null)
        {
            membersQuery = membersQuery.Where(member => member.UserId == targetUserId);
        }
        var members = await membersQuery.OrderBy(member => member.JoinedAt).ThenBy(member => member.Id)
            .Select(member => new ChatGeneralMemberState(member.Id, member.ChatRoomId, member.UserId, member.Role,
                member.Active, member.JoinedAt, member.LeftAt)).ToArrayAsync(cancellationToken);
        if (targetUserId is not null && members.Length == 0)
        {
            throw AccessDenied();
        }
        var disclosure = await context.ChatRoomAiSettings.Where(setting => setting.ChatRoomId == roomId)
            .Select(setting => setting.DisclosureType).SingleOrDefaultAsync(cancellationToken);

        // 공개 display DTO에 필요한 필드만 조회한다. 관리 settings 생성이나 persona 조회를 수행하지 않는다.
        var aiMembers = new List<ChatMemberAiDisplay>();
        if (targetUserId is null)
        {
            var aiRows = await (from member in context.ChatRoomAiMembers
                                join agent in context.ChatAiAgents on member.AiAgentId equals agent.Id
                                where member.ChatRoomId == roomId && member.Active && member.DeletedAt == null && agent.Active && agent.DeletedAt == null
                                orderby member.JoinedAt, member.Id
                                select new
                                {
                                    member.Id,
                                    agent.Nickname,
                                    agent.ProfileImageObjectKey,
                                    member.Active,
                                    member.JoinedAt
                                })
                .ToArrayAsync(cancellationToken);
            foreach (var ai in aiRows)
            {
                var url = IsBlankObjectKey(ai.ProfileImageObjectKey) ? null
                    : await (storage ?? throw new ChatMembershipDependencyUnavailableException()).ResolveUrlAsync(ai.ProfileImageObjectKey!, cancellationToken);
                aiMembers.Add(new(ai.Id, ai.Nickname, url, "MEMBER", ai.Active, ai.JoinedAt));
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return new(disclosure != "PRIVATE", members, aiMembers, aiMembers.Count == 0 ? null : disclosure ?? "PUBLIC");
    }

    private static ChatMembershipException AccessDenied()
    {
        return new("채팅방 멤버가 아니거나 접근 권한이 없습니다.", "CHAT_ROOM_MEMBER_ACCESS_DENIED");
    }

    private static bool IsBlankObjectKey(string? value)
    {
        return value is null || value.All(character => character is (>= '\u0009' and <= '\u000d') or (>= '\u001c' and <= '\u001f')
                || (char.GetUnicodeCategory(character) is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                    && character is not ('\u00a0' or '\u2007' or '\u202f')));
    }
}
