using System.Text.Json.Serialization;

namespace TranslaCat.Chat.Api.Read.Contracts;

// 원본의 nullable 필드는 전역 serializer 설정과 관계없이 JSON null로 남긴다.
public sealed record ChatRoomReadResponseDto(
    [property: JsonPropertyName("chatRoomId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long ChatRoomId,
    [property: JsonPropertyName("lastReadMessageId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long? LastReadMessageId,
    [property: JsonPropertyName("lastReadAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? LastReadAt,
    [property: JsonPropertyName("unreadCount"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long UnreadCount);

// 개인 recipient는 전송 담당의 정보이며 공개 JSON에 추가하지 않는다.
public sealed record ChatReadUpdatedEventDto(
    [property: JsonPropertyName("eventType"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string EventType,
    [property: JsonPropertyName("chatRoomId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long ChatRoomId,
    [property: JsonPropertyName("userId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long UserId,
    [property: JsonPropertyName("lastReadMessageId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long? LastReadMessageId,
    [property: JsonPropertyName("lastReadAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? LastReadAt,
    [property: JsonPropertyName("unreadCount"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long UnreadCount,
    [property: JsonPropertyName("occurredAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string OccurredAt);

public sealed record ChatMemberReadUpdatedEventDto(
    [property: JsonPropertyName("eventType"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string EventType,
    [property: JsonPropertyName("chatRoomId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long ChatRoomId,
    [property: JsonPropertyName("readerUserId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long? ReaderUserId,
    [property: JsonPropertyName("readerOpenChatMemberId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long? ReaderOpenChatMemberId,
    [property: JsonPropertyName("previousLastReadMessageId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long? PreviousLastReadMessageId,
    [property: JsonPropertyName("lastReadMessageId"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    long? LastReadMessageId,
    [property: JsonPropertyName("readAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? ReadAt,
    [property: JsonPropertyName("occurredAt"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string OccurredAt);
