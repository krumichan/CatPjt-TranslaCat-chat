using TranslaCat.Chat.Application.Notifications;

namespace TranslaCat.Chat.Application.OpenInvitations;

public sealed record OpenInvitationCreate(string? TargetPublicId, string? RequestId);
public sealed record OpenInvitationValidatedCreate(string TargetPublicId, Guid RequestId);
public sealed record OpenInvitationCreated(long InvitationId, long RoomId, DateTime CreatedAt, bool Duplicate);
public sealed record OpenInvitationSummary(long UnreadCount, ChatNotificationActivity? LatestInvitation);

public interface IOpenInvitationStore
{
    Task<OpenInvitationCreated> CreateAsync(long userId, long roomId, OpenInvitationValidatedCreate request,
        CancellationToken token);
    Task<IReadOnlyList<ChatNotificationActivity>> ListAsync(long userId, long? cursorId, int limit, CancellationToken token);
    Task<OpenInvitationSummary> SummaryAsync(long userId, CancellationToken token);
    Task<ChatNotificationActivity?> GetAsync(long userId, long invitationId, CancellationToken token);
    Task<ChatNotificationActivity?> ReadAsync(long userId, long invitationId, DateTime now, CancellationToken token);
}

public sealed class OpenInvitationException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class OpenInvitationDependencyUnavailableException : Exception;

public sealed class OpenInvitationService(IOpenInvitationStore store, Func<DateTime> clock)
{
    public Task<OpenInvitationCreated> CreateAsync(long userId, long roomId, OpenInvitationCreate request, CancellationToken token)
    {
        // 한 명의 정확한 공개 ID와 재시도 식별자만 받는다. 사용자 숫자 ID나 임의 URL은 받지 않는다.
        string target = request.TargetPublicId?.Trim() ?? "";
        if (roomId <= 0 || target.Length is < 1 or > 255
            || !Guid.TryParseExact(request.RequestId, "D", out var requestId) || requestId == Guid.Empty)
        {
            throw Error("초대 대상 ID와 요청 식별자를 확인해 주세요.", "INVALID_REQUEST");
        }
        return store.CreateAsync(userId, roomId, new(target, requestId), token);
    }

    public async Task<ChatNotificationActivityPage> ListAsync(long userId, long? cursorId, int? size, CancellationToken token)
    {
        if (cursorId is <= 0 || size is <= 0)
        {
            throw Error("초대 목록 조회 값을 확인해 주세요.", "INVALID_REQUEST");
        }
        int count = Math.Min(size ?? 20, 50);
        var fetched = await store.ListAsync(userId, cursorId, count + 1, token);
        bool hasNext = fetched.Count > count;
        var page = fetched.Take(count).ToArray();
        return new(page, hasNext ? page[^1].Id : null, hasNext);
    }

    public Task<OpenInvitationSummary> SummaryAsync(long userId, CancellationToken token) => store.SummaryAsync(userId, token);

    public async Task<ChatNotificationActivity> GetAsync(long userId, long invitationId, CancellationToken token)
    {
        return invitationId > 0 ? await store.GetAsync(userId, invitationId, token) ?? throw NotFound() : throw NotFound();
    }

    public async Task<ChatNotificationActivity> ReadAsync(long userId, long invitationId, CancellationToken token)
    {
        return invitationId > 0 ? await store.ReadAsync(userId, invitationId, clock(), token) ?? throw NotFound() : throw NotFound();
    }

    private static OpenInvitationException NotFound() => Error("초대를 찾을 수 없습니다.", "NOT_FOUND");
    public static OpenInvitationException Error(string message, string suffix) => new(message, "OPEN_INVITATION_" + suffix);
}
