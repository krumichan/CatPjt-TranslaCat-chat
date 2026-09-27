namespace TranslaCat.Chat.Application.Messaging;

public sealed partial class ChatMessageService
{
    public Task<ChatMessagePage> GetMessagesAsync(
        long userId, long roomId, long? cursorId = null, CancellationToken cancellationToken = default)
    {
        if (cursorId is <= 0)
        {
            throw new ChatMessageException("cursorId는 1 이상이어야 합니다.");
        }

        return transaction.ExecuteAsync(async (session, token) =>
        {
            var member = await session.GetMemberAsync(userId, roomId, false, token);

            // 이전 방향 cursor는 원본처럼 숫자 경계다. cursor 행 존재 검사는 추가하지 않는다.
            var fetched = await session.FetchAsync(member, cursorId, false, 101, token);
            bool hasNext = fetched.Count > 100;
            var page = fetched.Take(100).Reverse().ToArray();
            var messages = await session.PresentAsync(member, page, token);
            return new ChatMessagePage(messages, hasNext && page.Length > 0 ? page[0].Id : null, hasNext);
        }, cancellationToken);
    }

    public Task<ChatMessagePage> GetMessagesAfterAsync(
        long userId, long roomId, long? cursorId, int? size = null, CancellationToken cancellationToken = default)
    {
        long cursor = RequiredCursor(cursorId, "cursorId");
        int pageSize = size ?? 100;
        if (pageSize is <= 0 or > 100)
        {
            throw new ChatMessageException("size는 1 이상 100 이하여야 합니다.", "CHAT_MESSAGE_FORWARD_SIZE_INVALID");
        }

        return transaction.ExecuteAsync(async (session, token) =>
        {
            var member = await session.GetMemberAsync(userId, roomId, false, token);
            await RequireAccessibleAsync(session, member, cursor,
                "이후 메시지 조회 기준점을 찾을 수 없거나 접근할 수 없습니다.",
                "CHAT_MESSAGE_FORWARD_CURSOR_NOT_ACCESSIBLE", token);

            // 이후 방향은 가장 가까운 메시지부터 조회하고 마지막 반환 ID로 이어 간다.
            var fetched = await session.FetchAsync(member, cursor, true, pageSize + 1, token);
            bool hasNext = fetched.Count > pageSize;
            var page = fetched.Take(pageSize).ToArray();
            var messages = await session.PresentAsync(member, page, token);
            return new ChatMessagePage(messages, hasNext && page.Length > 0 ? page[^1].Id : null, hasNext);
        }, cancellationToken);
    }

    public Task<ChatMessageAnchorPage> GetMessagesAroundAnchorAsync(
        long userId, long roomId, long? anchorMessageId, int? beforeSize = null,
        int? afterSize = null, CancellationToken cancellationToken = default)
    {
        long anchorId = RequiredCursor(anchorMessageId, "anchorMessageId");
        int before = AnchorSize(beforeSize, 5, "beforeSize");
        int after = AnchorSize(afterSize, 30, "afterSize");

        return transaction.ExecuteAsync(async (session, token) =>
        {
            var member = await session.GetMemberAsync(userId, roomId, false, token);
            var anchor = await RequireAccessibleAsync(session, member, anchorId,
                "Anchor 메시지를 찾을 수 없거나 접근할 수 없습니다.",
                "CHAT_MESSAGE_ANCHOR_NOT_ACCESSIBLE", token);

            // 0개 요청도 한 행을 더 조회하여 해당 방향에 이어 볼 메시지가 있는지 판단한다.
            var previousFetched = await session.FetchAsync(member, anchorId, false, before + 1, token);
            var nextFetched = await session.FetchAsync(member, anchorId, true, after + 1, token);
            bool hasPrevious = previousFetched.Count > before;
            bool hasNext = nextFetched.Count > after;
            var previous = previousFetched.Take(before).Reverse().ToArray();
            var next = nextFetched.Take(after).ToArray();
            var page = previous.Concat([anchor]).Concat(next).ToArray();
            var messages = await session.PresentAsync(member, page, token);

            return new ChatMessageAnchorPage(messages, anchorId,
                hasPrevious ? (previous.Length > 0 ? previous[0].Id : anchorId) : null,
                hasPrevious, hasNext ? (next.Length > 0 ? next[^1].Id : anchorId) : null, hasNext);
        }, cancellationToken);
    }

    private static long RequiredCursor(long? messageId, string field)
    {
        if (messageId is null or <= 0)
        {
            throw new ChatMessageException($"{field}는 1 이상이어야 합니다.", "CHAT_MESSAGE_CURSOR_INVALID");
        }

        return messageId.Value;
    }

    private static int AnchorSize(int? requested, int defaultSize, string field)
    {
        int size = requested ?? defaultSize;
        if (size is < 0 or > 100)
        {
            throw new ChatMessageException($"{field}는 0 이상 100 이하여야 합니다.", "CHAT_MESSAGE_ANCHOR_SIZE_INVALID");
        }

        return size;
    }

    private static async Task<ChatStoredMessage> RequireAccessibleAsync(
        IChatMessageSession session, ChatMessageMember member, long id,
        string message, string code, CancellationToken cancellationToken)
    {
        return await session.FindAccessibleAsync(member, id, cancellationToken)
            ?? throw new ChatMessageException(message, code);
    }
}
