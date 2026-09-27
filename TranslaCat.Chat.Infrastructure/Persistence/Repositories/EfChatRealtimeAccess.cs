using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Read;
using TranslaCat.Chat.Application.Realtime;

namespace TranslaCat.Chat.Infrastructure.Persistence.Repositories;

public sealed class EfChatRealtimeAccess(IDbContextFactory<ChatDbContext> contexts) : IChatRealtimeAccess
{
    public Task ValidateRoomAccessAsync(long userId, long roomId, CancellationToken cancellationToken)
    {
        return ValidateAsync(userId, roomId, false, cancellationToken);
    }

    public Task ValidateRoomClosureDeliveryAsync(long userId, long roomId, CancellationToken cancellationToken)
    {
        return ValidateAsync(userId, roomId, true, cancellationToken);
    }

    private async Task ValidateAsync(long userId, long roomId, bool closureOnly, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var open = await context.OpenChatRooms.AsNoTracking()
            .SingleOrDefaultAsync(room => room.ChatRoomId == roomId, cancellationToken);

        // 원본 STOMP 접근은 OPEN의 차단과 종료 상태를 추가 검사한다. 읽음의 접근 정책과 구분한다.
        if (open is not null && await context.OpenChatBans.AnyAsync(ban => ban.ChatRoomId == roomId
                && ban.TargetUserId == userId && ban.ReleasedAt == null, cancellationToken))
        {
            throw new ChatReadException("OPEN_CHAT_BANNED", "해당 OPEN 채팅방에서 차단되어 접근할 수 없습니다.");
        }

        var membership = await (from member in context.ChatRoomMembers
                                join room in context.ChatRooms on member.ChatRoomId equals room.Id
                                where member.ChatRoomId == roomId && member.UserId == userId
                                    && member.Active && member.DeletedAt == null
                                select room.RoomType).SingleOrDefaultAsync(cancellationToken);
        if (membership is null)
        {
            throw new ChatReadException(open is null ? "CHAT_ROOM_MEMBER_ACCESS_DENIED" : "OPEN_CHAT_MEMBER_ACCESS_DENIED",
                "채팅방 멤버가 아니거나 접근 권한이 없습니다.");
        }

        if (open is not null)
        {
            if (membership != "OPEN")
            {
                throw new ChatReadException("OPEN_CHAT_ROOM_NOT_FOUND", "OPEN 채팅방을 찾을 수 없습니다.");
            }

            if (open.Status == "CLOSED" && !closureOnly)
            {
                throw new ChatReadException("OPEN_CHAT_ROOM_CLOSED", "종료된 OPEN 채팅방에서는 해당 작업을 수행할 수 없습니다.");
            }
        }

        if (closureOnly && (open?.Status != "CLOSED" || membership != "OPEN"))
        {
            throw new InvalidOperationException("종료 안내는 CLOSED OPEN 방에만 전달할 수 있습니다.");
        }
    }
}
