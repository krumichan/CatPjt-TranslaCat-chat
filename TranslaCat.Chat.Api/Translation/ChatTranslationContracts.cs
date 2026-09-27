using System.Text.Json.Serialization;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Translation;

namespace TranslaCat.Chat.Api.Translation;

public sealed record ChatTranslationCompletedEventDto(
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("chatRoomId")] long ChatRoomId,
    [property: JsonPropertyName("messageId")] long MessageId,
    [property: JsonPropertyName("translationId")] long TranslationId,
    [property: JsonPropertyName("languageCode")] string LanguageCode,
    [property: JsonPropertyName("translatedContent")] string? TranslatedContent,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("occurredAt")] string OccurredAt);

public sealed record ChatTranslationFailedEventDto(
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("chatRoomId")] long ChatRoomId,
    [property: JsonPropertyName("messageId")] long MessageId,
    [property: JsonPropertyName("translationId")] long TranslationId,
    [property: JsonPropertyName("languageCode")] string LanguageCode,
    [property: JsonPropertyName("translatedContent")] string? TranslatedContent,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("failureReason")] string? FailureReason,
    [property: JsonPropertyName("occurredAt")] string OccurredAt);

public sealed class ChatTranslationContractMapper(TimeZoneInfo sourceTimeZone)
{
    private readonly ChatReadTimestampFormatter timestamp = new(sourceTimeZone);

    public ChatMessageTranslationResponseDto ToResponse(ChatMessageTranslationView value)
    {
        return new(value.Id, value.LanguageCode, value.TranslatedContent, value.Status, value.FailureReason,
                value.CompletedAt is { } completed ? timestamp.Format(completed) : null);
    }

    public object ToEvent(ChatTranslationChanged value, DateTime occurredAt)
    {
        // 성공 이벤트에는 failureReason 필드가 없다. 실패 이벤트의 본문은 기존 번역이 있어도 null이다.
        return value.Status == "COMPLETED"
            ? new ChatTranslationCompletedEventDto("chat.translation.completed", value.ChatRoomId, value.MessageId,
                value.TranslationId, value.LanguageCode, value.TranslatedContent, value.Status, timestamp.Format(occurredAt))
            : new ChatTranslationFailedEventDto("chat.translation.failed", value.ChatRoomId, value.MessageId,
                value.TranslationId, value.LanguageCode, null, value.Status, value.FailureReason, timestamp.Format(occurredAt));
    }
}
