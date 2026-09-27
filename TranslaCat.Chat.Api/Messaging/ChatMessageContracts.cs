using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Api.Messaging;

public sealed record ChatMessageResponseDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("chatRoomId")] long ChatRoomId,
    [property: JsonPropertyName("senderUserId")] long? SenderUserId,
    [property: JsonPropertyName("senderAiMemberId")] long? SenderAiMemberId,
    [property: JsonPropertyName("senderName")] string? SenderName,
    [property: JsonPropertyName("senderEmail")] string? SenderEmail,
    [property: JsonPropertyName("senderProfileImageUrl")] string? SenderProfileImageUrl,
    [property: JsonPropertyName("senderType")] string SenderType,
    [property: JsonPropertyName("messageType")] string MessageType,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("unreadMemberCount")] long? UnreadMemberCount,
    [property: JsonPropertyName("translations")] IReadOnlyList<ChatMessageTranslationResponseDto> Translations,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt,
    [property: JsonPropertyName("sender")] OpenChatMessageSenderResponseDto? Sender);

public sealed record ChatMessageTranslationResponseDto(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("languageCode")] string LanguageCode,
    [property: JsonPropertyName("translatedContent")] string? TranslatedContent,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("failureReason")] string? FailureReason,
    [property: JsonPropertyName("completedAt")] string? CompletedAt);

public sealed record OpenChatMessageSenderResponseDto(
    [property: JsonPropertyName("openChatMemberId")] long OpenChatMemberId,
    [property: JsonPropertyName("memberCode")] string MemberCode,
    [property: JsonPropertyName("nickname")] string Nickname,
    [property: JsonPropertyName("profileImageUrl")] string? ProfileImageUrl,
    [property: JsonPropertyName("role")] string Role);

public sealed record ChatMessagePageResponseDto(
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessageResponseDto> Messages,
    [property: JsonPropertyName("nextCursorId")] long? NextCursorId,
    [property: JsonPropertyName("hasNext")] bool HasNext);

public sealed record ChatMessageAnchorResponseDto(
    [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessageResponseDto> Messages,
    [property: JsonPropertyName("anchorMessageId")] long AnchorMessageId,
    [property: JsonPropertyName("previousCursorId")] long? PreviousCursorId,
    [property: JsonPropertyName("hasPrevious")] bool HasPrevious,
    [property: JsonPropertyName("nextCursorId")] long? NextCursorId,
    [property: JsonPropertyName("hasNext")] bool HasNext);

public sealed record ChatMessageCreatedEventDto(
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("chatRoomId")] long ChatRoomId,
    [property: JsonPropertyName("message")] ChatMessageResponseDto Message,
    [property: JsonPropertyName("occurredAt")] string OccurredAt);

public sealed class ChatMessageContractMapper(TimeZoneInfo sourceTimeZone)
{
    private readonly ChatReadTimestampFormatter timestamp = new(sourceTimeZone);

    public ChatMessageResponseDto ToResponse(ChatMessageView value)
    {
        // Application이 결정한 OPEN 식별자/익명 필드와 nullable 집계를 그대로 보존한다.
        return new(value.Id, value.ChatRoomId, value.SenderUserId, value.SenderAiMemberId,
            value.SenderName, value.SenderEmail, value.SenderProfileImageUrl,
            value.SenderType, value.MessageType, value.Content, value.Status, value.UnreadMemberCount,
            value.Translations.Select(item => new ChatMessageTranslationResponseDto(
                item.Id, item.LanguageCode, item.TranslatedContent, item.Status, item.FailureReason,
                item.CompletedAt is { } completed ? timestamp.Format(completed) : null)).ToArray(),
            timestamp.Format(value.CreatedAt), timestamp.Format(value.UpdatedAt),
            value.Sender is { } sender ? new OpenChatMessageSenderResponseDto(
                sender.OpenChatMemberId, sender.MemberCode, sender.Nickname, sender.ProfileImageUrl, sender.Role) : null);
    }

    public ChatMessagePageResponseDto ToResponse(ChatMessagePage value)
    {
        return new(value.Messages.Select(ToResponse).ToArray(), value.NextCursorId, value.HasNext);
    }

    public ChatMessageAnchorResponseDto ToResponse(ChatMessageAnchorPage value)
    {
        return new(value.Messages.Select(ToResponse).ToArray(), value.AnchorMessageId,
                value.PreviousCursorId, value.HasPrevious, value.NextCursorId, value.HasNext);
    }

    public ChatMessageCreatedEventDto ToCreatedEvent(ChatMessageView value, DateTime occurredAt)
    {
        return new("chat.message.created", value.ChatRoomId, ToResponse(value), timestamp.Format(occurredAt));
    }
}

public sealed class ChatMessageCreateRequestDto : IValidatableObject
{
    [JsonPropertyName("content")]
    [JsonConverter(typeof(ChatMessageContentConverter))]
    public string? Content
    {
        get; init;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // 현재 Hibernate @NotBlank는 Java trim을 사용한다. API는 trim 전 원문 @Size도 검사한다.
        if (Content is null || Content.All(value => value <= '\u0020') || Content.Length > 5000)
        {
            yield return new ValidationResult("메시지 내용은 1~5000자의 유효한 원문이어야 합니다.", [nameof(Content)]);
        }
    }
}

public sealed class ChatMessageContentConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Jackson String DTO는 숫자/boolean scalar를 문자열로 받는다. 임의 객체 직렬화는 허용하지 않는다.
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }
        if (reader.TokenType is JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False)
        {
            using var scalar = JsonDocument.ParseValue(ref reader);
            return scalar.RootElement.GetRawText();
        }
        throw new JsonException("Invalid message content.");
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
