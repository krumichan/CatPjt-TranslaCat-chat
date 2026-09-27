using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TranslaCat.Chat.Api.Language;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.OpenRooms;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Api.AiManagement;

public sealed class ChatAiProfileDto : IValidatableObject
{
    [JsonConverter(typeof(ChatMessageContentConverter))]
    public string? Nickname
    {
        get; init;
    }
    [JsonConverter(typeof(ChatMessageContentConverter))]
    public string? Bio
    {
        get; init;
    }
    [JsonConverter(typeof(ChatMessageContentConverter))]
    public string? OriginalLanguageCode
    {
        get; init;
    }
    [JsonConverter(typeof(ChatMessageContentConverter))]
    public string? PersonaPrompt
    {
        get; init;
    }

    public ChatAiProfileInput ToInput()
    {
        return new(Nickname, Bio, OriginalLanguageCode, PersonaPrompt);
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        // 원본 @Valid는 trim 이전 길이와 필수값을 검사한다.
        if (ChatMessageText.IsBlank(Nickname) || Nickname?.Length > 50 || Bio?.Length > 200
            || ChatMessageText.IsBlank(OriginalLanguageCode) || OriginalLanguageCode?.Length > 10
            || ChatMessageText.IsBlank(PersonaPrompt) || PersonaPrompt?.Length > 4000)
        {
            yield return new ValidationResult("Invalid AI profile.");
        }
    }
}

public sealed class ChatAiRoomPatchDto
{
    [JsonConverter(typeof(ChatAiDisclosureConverter))]
    public string? DisclosureType
    {
        get; init;
    }
    [JsonConverter(typeof(ChatAiMentionConverter))]
    public string? MentionPermission
    {
        get; init;
    }
    [JsonConverter(typeof(ChatLanguageBooleanConverter))]
    public bool? ConversationEnabled
    {
        get; init;
    }
    [JsonConverter(typeof(ChatLanguageBooleanConverter))]
    public bool? RevivalEnabled
    {
        get; init;
    }

    public ChatAiRoomPatch ToPatch()
    {
        return new(DisclosureType, MentionPermission, ConversationEnabled, RevivalEnabled);
    }
}

public abstract class ChatAiEnumConverter(params string[] values) : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int ordinal))
        {
            return ordinal >= 0 && ordinal < values.Length ? values[ordinal] : throw new JsonException();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var text = ChatMessageText.Trim(reader.GetString()!);
            if (values.Contains(text, StringComparer.Ordinal))
            {
                return text;
            }

            if (text.Length == 1 && text[0] >= '0' && text[0] < '0' + values.Length)
            {
                return values[text[0] - '0'];
            }
        }
        throw new JsonException();
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
public sealed class ChatAiDisclosureConverter() : ChatAiEnumConverter("PUBLIC", "PRIVATE");
public sealed class ChatAiMentionConverter() : ChatAiEnumConverter("ALL_MEMBERS", "OWNER_ADMIN_ONLY");

public sealed class ChatAiSystemPatchDto
{
    [JsonConverter(typeof(OpenIntegerConverter))]
    public int? MaxAiMembersPerRoom
    {
        get; init;
    }

    [JsonConverter(typeof(OpenIntegerConverter))]
    public int? ConversationResponseRate
    {
        get; init;
    }

    [JsonConverter(typeof(OpenIntegerConverter))]
    public int? ConversationCooldownSeconds
    {
        get; init;
    }

    [JsonConverter(typeof(OpenIntegerConverter))]
    public int? ConversationMinHumanMessagesAfterAi
    {
        get; init;
    }

    [JsonConverter(typeof(ChatLanguageBooleanConverter))]
    public bool? ResponseDelayEnabled
    {
        get; init;
    }

    [JsonConverter(typeof(OpenIntegerConverter))]
    public int? ResponseDelayMinMillis
    {
        get; init;
    }

    [JsonConverter(typeof(OpenIntegerConverter))]
    public int? ResponseDelayMaxMillis
    {
        get; init;
    }

    [JsonConverter(typeof(OpenIntegerConverter))]
    public int? RevivalFirstDelayHours
    {
        get; init;
    }

    [JsonConverter(typeof(OpenIntegerConverter))]
    public int? RevivalSecondDelayHours
    {
        get; init;
    }

    [JsonConverter(typeof(OpenIntegerConverter))]
    public int? RevivalThirdDelayHours
    {
        get; init;
    }

    [JsonConverter(typeof(ChatAiLocalTimeConverter))]
    public TimeSpan? RevivalAllowedStartTime
    {
        get; init;
    }

    [JsonConverter(typeof(ChatAiLocalTimeConverter))]
    public TimeSpan? RevivalAllowedEndTime
    {
        get; init;
    }

    [JsonConverter(typeof(OpenIntegerConverter))]
    public int? ContextMaxMessages
    {
        get; init;
    }

    [JsonConverter(typeof(OpenIntegerConverter))]
    public int? ContextMaxCharacters
    {
        get; init;
    }

    [JsonConverter(typeof(OpenIntegerConverter))]
    public int? ReplyMaxCharacters
    {
        get; init;
    }

    [JsonConverter(typeof(OpenIntegerConverter))]
    public int? MentionRateLimitCount
    {
        get; init;
    }

    [JsonConverter(typeof(OpenIntegerConverter))]
    public int? MentionRateLimitWindowSeconds
    {
        get; init;
    }

    public ChatAiSystemPatch ToPatch()
    {
        return new()
        {
            MaxAiMembersPerRoom = MaxAiMembersPerRoom,
            ConversationResponseRate = ConversationResponseRate,
            ConversationCooldownSeconds = ConversationCooldownSeconds,
            ConversationMinHumanMessagesAfterAi = ConversationMinHumanMessagesAfterAi,
            ResponseDelayEnabled = ResponseDelayEnabled,
            ResponseDelayMinMillis = ResponseDelayMinMillis,
            ResponseDelayMaxMillis = ResponseDelayMaxMillis,
            RevivalFirstDelayHours = RevivalFirstDelayHours,
            RevivalSecondDelayHours = RevivalSecondDelayHours,
            RevivalThirdDelayHours = RevivalThirdDelayHours,
            RevivalAllowedStartTime = RevivalAllowedStartTime,
            RevivalAllowedEndTime = RevivalAllowedEndTime,
            ContextMaxMessages = ContextMaxMessages,
            ContextMaxCharacters = ContextMaxCharacters,
            ReplyMaxCharacters = ReplyMaxCharacters,
            MentionRateLimitCount = MentionRateLimitCount,
            MentionRateLimitWindowSeconds = MentionRateLimitWindowSeconds,
        };
    }
}

public sealed class ChatAiLocalTimeConverter : JsonConverter<TimeSpan?>
{
    public override TimeSpan? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString()!.Trim();
            if (value.Length == 0)
            {
                return null;
            }

            var parts = value.Split(':');
            if (parts.Length is < 2 or > 3 || parts[0].Length != 2 || parts[1].Length != 2)
            {
                throw new JsonException();
            }

            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hour)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minute))
            {
                throw new JsonException();
            }

            int second = 0, nanos = 0;
            if (parts.Length == 3)
            {
                var seconds = parts[2].Split('.');
                if (seconds.Length > 2 || seconds[0].Length != 2
                    || !int.TryParse(seconds[0], NumberStyles.None, CultureInfo.InvariantCulture, out second))
                {
                    throw new JsonException();
                }

                if (seconds.Length == 2)
                {
                    if (seconds[1].Length is < 1 or > 9 || !seconds[1].All(char.IsAsciiDigit))
                    {
                        throw new JsonException();
                    }

                    nanos = int.Parse(seconds[1].PadRight(9, '0'), CultureInfo.InvariantCulture);
                }
            }
            return Create(hour, minute, second, nanos);
        }
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            using var array = JsonDocument.ParseValue(ref reader);
            var values = array.RootElement.EnumerateArray().Select(item => item.GetInt32()).ToArray();
            if (values.Length is < 2 or > 4)
            {
                throw new JsonException();
            }

            return Create(values[0], values[1], values.Length > 2 ? values[2] : 0, values.Length > 3 ? values[3] : 0);
        }
        throw new JsonException();
    }

    private static TimeSpan Create(int hour, int minute, int second, int nanos)
    {
        // 100ns보다 작은 입력을 조용히 절삭하지 않는다. Java nanosecond 전체 계약은 별도 결정 항목이다.
        if (hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59 || nanos is < 0 or > 999999999 || nanos % 100 != 0)
        {
            throw new JsonException();
        }

        return new TimeSpan(hour, minute, second) + TimeSpan.FromTicks(nanos / 100);
    }

    public override void Write(Utf8JsonWriter writer, TimeSpan? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(Format(value.Value));
        }
    }
    public static string Format(TimeSpan value)
    {
        return DateTime.MinValue.Add(value).ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.');
    }
}

public sealed class ChatAiManagementMapper(ChatReadLocalTime local)
{
    private readonly ChatReadTimestampFormatter times = new(local.SourceTimeZone);

    public object Member(ChatAiMember value)
    {
        return new
        {
            value.AiMemberId,
            value.AiAgentId,
            value.ChatRoomId,
            value.Nickname,
            value.ProfileImageUrl,
            value.ProfileBackgroundImageUrl,
            value.Bio,
            value.OriginalLanguageCode,
            value.PersonaPrompt,
            value.Active,
            joinedAt = times.Format(value.JoinedAt),
            createdAt = times.Format(value.CreatedAt),
            updatedAt = times.Format(value.UpdatedAt)
        };
    }

    public object Safe(ChatAiMember value)
    {
        return new
        {
            value.AiMemberId,
            value.Nickname,
            value.ProfileImageUrl,
            value.ProfileBackgroundImageUrl,
            value.Bio,
            value.OriginalLanguageCode,
            value.Active,
            joinedAt = times.Format(value.JoinedAt)
        };
    }

    public object List(ChatAiMemberList value)
    {
        return new
        {
            value.ChatRoomId,
            value.CurrentCount,
            value.MaxCount,
            members = value.Members.Select(Member).ToArray()
        };
    }

    public object SystemSettings(ChatAiSystemSettings value)
    {
        return new
        {
            value.MaxAiMembersPerRoom,
            value.ConversationResponseRate,
            value.ConversationCooldownSeconds,
            value.ConversationMinHumanMessagesAfterAi,
            value.ResponseDelayEnabled,
            value.ResponseDelayMinMillis,
            value.ResponseDelayMaxMillis,
            value.RevivalFirstDelayHours,
            value.RevivalSecondDelayHours,
            value.RevivalThirdDelayHours,
            revivalAllowedStartTime = ChatAiLocalTimeConverter.Format(value.RevivalAllowedStartTime),
            revivalAllowedEndTime = ChatAiLocalTimeConverter.Format(value.RevivalAllowedEndTime),
            value.ContextMaxMessages,
            value.ContextMaxCharacters,
            value.ReplyMaxCharacters,
            value.MentionRateLimitCount,
            value.MentionRateLimitWindowSeconds,
        };
    }
}

