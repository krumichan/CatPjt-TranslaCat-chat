using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.OpenRooms;

namespace TranslaCat.Chat.Api.OpenRooms;

public sealed class OpenRoomCreateDto : IValidatableObject
{
    [JsonConverter(typeof(ChatMessageContentConverter))]
    public string? Name
    {
        get; init;
    }
    [JsonConverter(typeof(ChatMessageContentConverter))]
    public string? Description
    {
        get; init;
    }
    [JsonConverter(typeof(OpenVisibilityConverter))]
    public string? Visibility
    {
        get; init;
    }
    [JsonConverter(typeof(OpenIntegerConverter))]
    public int? MaxMemberCount
    {
        get; init;
    }
    public OpenOwnerProfileDto? OwnerProfile
    {
        get; init;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Bean Validation은 service trim 이전의 입력 길이를 검사한다.
        if (ChatMessageText.IsBlank(Name) || Name?.Length > 100 || ChatMessageText.IsBlank(Description)
            || Description?.Length > 500 || Visibility is not ("PUBLIC" or "UNLISTED")
            || MaxMemberCount is < 2 or > 100 || OwnerProfile is null
            || ChatMessageText.IsBlank(OwnerProfile.Nickname) || OwnerProfile.Nickname?.Length > 50
            || OwnerProfile.ProfileImageObjectKey?.Length > 500)
        {
            yield return new ValidationResult("Invalid OPEN room request.");
        }
    }
}

public sealed class OpenOwnerProfileDto
{
    [JsonConverter(typeof(ChatMessageContentConverter))]
    public string? Nickname
    {
        get; init;
    }
    [JsonConverter(typeof(ChatMessageContentConverter))]
    public string? ProfileImageObjectKey
    {
        get; init;
    }
}

public sealed class OpenProfileUpdateDto
{
    [JsonConverter(typeof(ChatMessageContentConverter))]
    public string? Nickname
    {
        get; init;
    }
}

public sealed class OpenVisibilityConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number))
        {
            return ConvertOrdinal(number);
        }
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = ChatMessageText.Trim(reader.GetString()!);
            return value is "0" or "1" ? ConvertOrdinal(value[0] - '0') : value;
        }
        throw new JsonException();
    }
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }

    private static string ConvertOrdinal(int value)
    {
        return value switch
        {
            0 => "PUBLIC",
            1 => "UNLISTED",
            _ => throw new JsonException()
        };
    }
}

public sealed class OpenIntegerConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = new ChatReadMessageIdConverter().Read(ref reader, typeof(long?), options);
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw new JsonException();
        }
        return value is null ? null : (int)value.Value;
    }
    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteNumberValue(value.Value);
        }
    }
}

public sealed class OpenRoomContractMapper(ChatReadLocalTime localTime)
{
    private readonly ChatReadTimestampFormatter timestamps = new(localTime.SourceTimeZone);

    public object? Profile(OpenProfile? profile)
    {
        return profile is null ? null : new
        {
            profile.OpenChatMemberId,
            profile.MemberCode,
            profile.Nickname,
            profile.ProfileImageUrl,
            profile.Role,
            profile.Active,
            profile.Online,
            joinedAt = timestamps.Format(profile.JoinedAt)
        };
    }

    public object Detail(OpenRoomDetail room)
    {
        return new
        {
            room.Id,
            room.RoomType,
            room.SourceType,
            room.Name,
            room.Description,
            room.Visibility,
            room.Status,
            room.MemberCount,
            room.MaxMemberCount,
            room.Joined,
            room.Joinable,
            room.JoinBlockedReason,
            room.MyRole,
            ownerProfile = Profile(room.OwnerProfile),
            myOpenProfile = Profile(room.MyOpenProfile),
            lastActivityAt = timestamps.Format(room.LastActivityAt),
            createdAt = timestamps.Format(room.CreatedAt),
            updatedAt = timestamps.Format(room.UpdatedAt),
            room.Ai
        };
    }

    public object List(OpenRoomList page)
    {
        return new
        {
            openChatRooms = page.Rooms.Select(room => new
            {
                room.Id,
                room.RoomType,
                room.SourceType,
                room.Name,
                room.Description,
                room.Visibility,
                room.Status,
                room.MemberCount,
                room.MaxMemberCount,
                room.Joined,
                room.Joinable,
                room.JoinBlockedReason,
                lastActivityAt = timestamps.Format(room.LastActivityAt),
                ownerProfile = Profile(room.OwnerProfile),
                room.Ai
            }).ToArray(),
            page.NextCursorId,
            page.HasNext
        };
    }

    public object Members(OpenMemberList value)
    {
        return new
        {
            members = value.Members.Select(Profile).ToArray(),
            aiMembers = value.AiMembers.Select(member => new
            {
                member.AiMemberId,
                member.Nickname,
                member.ProfileImageUrl,
                member.Role,
                member.Active,
                joinedAt = timestamps.Format(member.JoinedAt)
            }).ToArray(),
            value.AiDisclosureType
        };
    }

    public object Event(OpenProfileChanged change)
    {
        return new
        {
            eventType = "chat.open-profile.updated",
            change.RoomId,
            change.OpenChatMemberId,
            change.MemberCode,
            change.Nickname,
            change.ProfileImageUrl,
            change.Role,
            occurredAt = timestamps.Format(change.OccurredAt)
        };
    }
}
