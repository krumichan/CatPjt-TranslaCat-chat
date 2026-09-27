using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Presence;

namespace TranslaCat.Chat.Infrastructure.Persistence.Repositories;

public sealed class EfChatPresenceRoomReader(IDbContextFactory<ChatDbContext> contexts) : IChatPresenceRoomReader
{
    public async Task<IReadOnlyList<ChatPresenceRoomMember>> FindActiveMembershipsAsync(long userId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // CHAT 소유 테이블만 조회한다. 계정 publicId는 별도 외부 profile port에서 확인한다.
        return await (
            from member in context.ChatRoomMembers.AsNoTracking()
            join room in context.ChatRooms.AsNoTracking() on member.ChatRoomId equals room.Id
            where member.UserId == userId && member.Active && member.DeletedAt == null
            select new ChatPresenceRoomMember(
                room.Id,
                room.RoomType,
                member.Id,
                context.ChatRoomAiSettings.Any(setting => setting.ChatRoomId == room.Id && setting.DisclosureType == "PRIVATE")))
            .ToListAsync(cancellationToken);
    }
}
