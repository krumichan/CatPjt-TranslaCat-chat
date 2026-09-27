using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace TranslaCat.Chat.Api.Read.Contracts;

// HTTP 필수/양수 검증이다. Application과 Domain의 nullable 정책을 바꾸지 않는다.
public sealed class ChatRoomReadRequestDto
{
    [Required]
    [Range(typeof(long), "1", "9223372036854775807", ParseLimitsInInvariantCulture = true)]
    [JsonPropertyName("lastReadMessageId")]
    [JsonConverter(typeof(ChatReadMessageIdConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? LastReadMessageId
    {
        get; init;
    }
}
