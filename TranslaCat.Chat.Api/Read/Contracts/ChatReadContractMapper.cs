using TranslaCat.Chat.Application.Read;

namespace TranslaCat.Chat.Api.Read.Contracts;

public sealed class ChatReadContractMapper(TimeZoneInfo sourceTimeZone)
{
    private readonly ChatReadTimestampFormatter timestamp = new(sourceTimeZone);

    public ChatRoomReadResponseDto ToResponse(ChatRoomReadResponse response)
    {
        // Application이 결정한 cursor, nullable 시각, 집계 결과를 그대로 옮긴다.
        return new ChatRoomReadResponseDto(
            response.ChatRoomId,
            response.LastReadMessageId,
            FormatNullable(response.LastReadAt),
            response.UnreadCount);
    }

    public ChatReadUpdatedEventDto ToSelfEvent(ChatReadUpdated readUpdated, DateTime occurredAt)
    {
        // occurredAt은 호출자가 commit 이후 DTO 작성 시점에 제공한다. 여기서 시계를 읽지 않는다.
        var response = readUpdated.Response;
        return new ChatReadUpdatedEventDto(
            "chat.read.updated",
            response.ChatRoomId,
            readUpdated.UserId,
            response.LastReadMessageId,
            FormatNullable(response.LastReadAt),
            response.UnreadCount,
            timestamp.Format(occurredAt));
    }

    public ChatMemberReadUpdatedEventDto ToMemberEvent(ChatMemberReadUpdated memberReadUpdated, DateTime occurredAt)
    {
        // OPEN과 일반 room의 식별자는 Application snapshot을 보존하며 서로 대체하지 않는다.
        return new ChatMemberReadUpdatedEventDto(
            "chat.member.read.updated",
            memberReadUpdated.ChatRoomId,
            memberReadUpdated.ReaderUserId,
            memberReadUpdated.ReaderOpenChatMemberId,
            memberReadUpdated.PreviousLastReadMessageId,
            memberReadUpdated.LastReadMessageId,
            FormatNullable(memberReadUpdated.ReadAt),
            timestamp.Format(occurredAt));
    }

    private string? FormatNullable(DateTime? value)
    {
        return value is DateTime timestampValue
        ? timestamp.Format(timestampValue)
        : null;
    }
}
