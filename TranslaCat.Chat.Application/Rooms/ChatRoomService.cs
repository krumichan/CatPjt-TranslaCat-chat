namespace TranslaCat.Chat.Application.Rooms;

public sealed class ChatRoomService(IChatRoomStore store, Func<DateTime> clock)
{
    public async Task<ChatRoomView> CreateAsync(long userId, ChatRoomCreateRequest request, CancellationToken cancellationToken = default)
    {
        // 원본 일반 생성 API는 OPEN을 거절한다. 친구/OPEN 전용 API의 권한을 이 경로에 섞지 않는다.
        if (request.RoomType is null)
        {
            throw new ChatRoomException("채팅방 타입은 필수입니다.");
        }
        if (request.RoomType == "OPEN")
        {
            throw new ChatRoomException("OPEN 채팅방은 전용 API를 사용해야 합니다.");
        }
        if (request.RoomType is not ("DIRECT" or "GROUP"))
        {
            throw new ChatRoomException("지원하지 않는 채팅방 타입입니다.");
        }
        if (request.MemberUserIds is null || request.MemberUserIds.Count == 0)
        {
            throw new ChatRoomException("채팅방 멤버는 최소 1명 이상 필요합니다.");
        }

        // Java LinkedHashSet처럼 최초 순서를 유지하며 중복만 제거한다. null 검사는 계정 조회 경계에 남긴다.
        var members = request.MemberUserIds.Distinct().ToArray();
        if (members.Contains(userId))
        {
            throw new ChatRoomException("채팅방 생성자는 멤버 목록에 포함하지 않습니다.");
        }
        if (request.RoomType == "DIRECT" && members.Length != 1)
        {
            throw new ChatRoomException("1:1 채팅방은 상대방 1명만 지정할 수 있습니다.");
        }

        var roomId = await store.CreateOrReuseAsync(userId, request, members, clock(), cancellationToken);
        // 원본 facade처럼 저장 transaction 이후 최신 상세를 조회한다.
        return await store.GetAsync(userId, roomId, cancellationToken);
    }

    public Task<ChatRoomView> GetAsync(long userId, long roomId, CancellationToken cancellationToken = default)
    {
        return store.GetAsync(userId, roomId, cancellationToken);
    }

    public Task<IReadOnlyList<ChatRoomListItem>> ListAsync(long userId, CancellationToken cancellationToken = default)
    {
        return store.ListAsync(userId, cancellationToken);
    }
}
