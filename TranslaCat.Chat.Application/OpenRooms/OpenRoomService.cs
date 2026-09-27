namespace TranslaCat.Chat.Application.OpenRooms;

public sealed class OpenRoomService(IOpenRoomStore store, Func<DateTime> clock)
{
    public Task<OpenRoomDetail> CreateAsync(long userId, OpenRoomCreate? request, CancellationToken cancellationToken)
    {
        return store.CreateAsync(userId, OpenRoomPolicy.ValidateCreate(request), clock(), cancellationToken);
    }

    public Task<OpenRoomDetail> GetDetailAsync(long userId, long roomId, CancellationToken cancellationToken)
    {
        return store.GetDetailAsync(userId, roomId, cancellationToken);
    }

    public Task<OpenRoomList> ListAsync(long userId, string? keyword, long? cursorId, int? size, CancellationToken cancellationToken)
    {
        var normalized = OpenRoomPolicy.NormalizeKeyword(keyword);
        if (cursorId is <= 0)
        {
            throw OpenRoomPolicy.Error("cursorId는 1 이상이어야 합니다.", "CURSOR_INVALID");
        }
        int requested = size ?? 20;
        if (requested is <= 0 or > 50)
        {
            throw OpenRoomPolicy.Error("size는 1 이상 50 이하여야 합니다.", "PAGE_SIZE_INVALID");
        }
        return store.ListAsync(userId, normalized, cursorId, requested, cancellationToken);
    }

    public Task<OpenProfile> GetProfileAsync(long userId, long roomId, long? memberId, CancellationToken cancellationToken)
    {
        return store.GetProfileAsync(userId, roomId, memberId, cancellationToken);
    }

    public Task<OpenMemberList> GetMembersAsync(long userId, long roomId, CancellationToken cancellationToken)
    {
        return store.GetMembersAsync(userId, roomId, cancellationToken);
    }

    // 원본처럼 접근/종료 검증이 nickname 정규화보다 먼저 실행되도록 store 안에서 정책을 호출한다.
    public Task<OpenProfile> UpdateNicknameAsync(long userId, long roomId, string? nickname, CancellationToken cancellationToken)
    {
        return store.UpdateNicknameAsync(userId, roomId, nickname, clock(), cancellationToken);
    }

    public Task<OpenProfile> DeleteImageAsync(long userId, long roomId, CancellationToken cancellationToken)
    {
        return store.DeleteImageAsync(userId, roomId, clock(), cancellationToken);
    }
}
