using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.Rooms;

namespace TranslaCat.Chat.Api.Rooms;

public sealed class ChatRoomCreateDto : IValidatableObject
{
    [Required]
    [JsonConverter(typeof(ChatRoomTypeConverter))]
    public string? RoomType
    {
        get; init;
    }
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
    [Required, JsonConverter(typeof(ChatRoomMemberIdsConverter))]
    public IReadOnlyList<long?>? MemberUserIds
    {
        get; init;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (RoomType is not ("DIRECT" or "GROUP" or "OPEN"))
        {
            yield return new ValidationResult("Invalid room type.", [nameof(RoomType)]);
        }
    }
}

public sealed class ChatRoomTypeConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Jackson enum의 선언 순서 ordinal과 trim된 enum 이름을 보존한다.
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var ordinal))
        {
            return FromOrdinal(ordinal);
        }
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = TranslaCat.Chat.Application.Messaging.ChatMessageText.Trim(reader.GetString()!);
            return value is "0" or "1" or "2" ? FromOrdinal(value[0] - '0') : value;
        }
        throw new JsonException();
    }
    private static string FromOrdinal(int ordinal)
    {
        return ordinal switch
        {
            0 => "DIRECT",
            1 => "GROUP",
            2 => "OPEN",
            _ => throw new JsonException()
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

public sealed class ChatRoomMemberIdsConverter : JsonConverter<IReadOnlyList<long?>>
{
    public override IReadOnlyList<long?> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException();
        }
        var converter = new ChatReadMessageIdConverter();
        var ids = new List<long?>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            ids.Add(converter.Read(ref reader, typeof(long?), options));
        }
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException();
        }
        return ids.AsReadOnly();
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<long?> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}

public sealed class ChatRoomContractMapper(ChatReadLocalTime localTime)
{
    private readonly ChatReadTimestampFormatter timestamps = new(localTime.SourceTimeZone);

    public object Detail(ChatRoomView room)
    {
        return new
        {
            room.Id,
            room.RoomType,
            room.SourceType,
            room.Name,
            room.Description,
            room.OwnerId,
            room.MemberCount,
            room.Active,
            room.OriginalLanguageCode,
            room.TranslationLanguageCode,
            room.RoomLanguageSettingApplied,
            createdAt = timestamps.Format(room.CreatedAt),
            updatedAt = timestamps.Format(room.UpdatedAt),
            room.MyRole,
            room.DirectPartner
        };
    }

    public object List(IReadOnlyList<ChatRoomListItem> rooms)
    {
        return new
        {
            chatRooms = rooms.Select(room => new
            {
                room.Id,
                room.RoomType,
                room.SourceType,
                room.Name,
                room.Description,
                room.OwnerId,
                room.MemberCount,
                room.UnreadCount,
                createdAt = timestamps.Format(room.CreatedAt),
                updatedAt = timestamps.Format(room.UpdatedAt),
                room.DirectPartner
            }).ToArray()
        };
    }
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class ChatRoomHttpBoundaryAttribute : ActionFilterAttribute, IExceptionFilter
{
    public ChatRoomHttpBoundaryAttribute()
    {
        Order = -3200;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            context.Result = Error(context.HttpContext, 500, "", "채팅방 요청 형식 또는 입력 값이 올바르지 않습니다.");
        }
    }

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        var (status, code, message) = context.Exception switch
        {
            ChatRoomException business => (400, business.Code, business.Message),
            ChatRoomDependencyUnavailableException => (503, "CHAT_ROOM_UNAVAILABLE", "채팅방 의존성이 구성되지 않았습니다."),
            _ => (500, "", "채팅방 요청을 처리하지 못했습니다.")
        };
        context.Result = Error(context.HttpContext, status, code, message);
        context.ExceptionHandled = true;
    }

    private static ObjectResult Error(HttpContext context, int status, string code, string message)
    {
        return new(context.RequestServices.GetRequiredService<ChatReadHttpResponses>().Error(context, status, code, $"Message <{message}>"))
        {
            StatusCode = status
        };
    }
}
