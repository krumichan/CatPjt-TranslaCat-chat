using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.Language;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.Language;

namespace TranslaCat.Chat.Api.Language;

public sealed class ChatLanguageUpdateDto
{
    [JsonPropertyName("originalLanguageCode"), JsonConverter(typeof(ChatMessageContentConverter))]
    public string? OriginalLanguageCode
    {
        get; init;
    }

    [JsonPropertyName("translationLanguageCode"), JsonConverter(typeof(ChatMessageContentConverter))]
    public string? TranslationLanguageCode
    {
        get; init;
    }

    [JsonPropertyName("showOriginal"), JsonConverter(typeof(ChatLanguageBooleanConverter))]
    public bool? ShowOriginal
    {
        get; init;
    }

    [JsonPropertyName("showTranslation"), JsonConverter(typeof(ChatLanguageBooleanConverter))]
    public bool? ShowTranslation
    {
        get; init;
    }

    public ChatLanguageUpdate ToRequest()
    {
        return new(OriginalLanguageCode, TranslationLanguageCode, ShowOriginal, ShowTranslation);
    }
}

public sealed record ChatDefaultLanguageResponse(long UserId, string OriginalLanguageCode, string TranslationLanguageCode,
    bool ShowOriginal, bool ShowTranslation, string Source);
public sealed record ChatRoomLanguageResponse(long ChatRoomId, long UserId, string OriginalLanguageCode, string TranslationLanguageCode,
    bool ShowOriginal, bool ShowTranslation, bool RoomLanguageSettingApplied, string Source);

[ApiController, ChatReadEndpoint, ChatLanguageHttpBoundary, Authorize(Policy = ChatReadAuthorization.Policy)]
public sealed class ChatLanguageController(ChatLanguageService service, ChatReadHttpResponses responses) : ControllerBase
{
    [HttpGet("/api/v1/users/me/chat-language-settings")]
    public async Task<IActionResult> GetDefault(CancellationToken cancellationToken)
    {
        return Default(await service.GetDefaultAsync(UserId(), cancellationToken));
    }

    [HttpPatch("/api/v1/users/me/chat-language-settings")]
    public async Task<IActionResult> UpdateDefault([FromBody] ChatLanguageUpdateDto request, CancellationToken cancellationToken)
    {
        return Default(await service.UpdateDefaultAsync(UserId(), request.ToRequest(), cancellationToken));
    }

    [HttpGet("/api/v1/chat/rooms/{chatRoomId}/language-settings")]
    public async Task<IActionResult> GetRoom([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long chatRoomId, CancellationToken cancellationToken)
    {
        return Room(chatRoomId, await service.GetRoomAsync(UserId(), chatRoomId, cancellationToken));
    }

    [HttpPatch("/api/v1/chat/rooms/{chatRoomId}/language-settings")]
    public async Task<IActionResult> UpdateRoom([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long chatRoomId,
        [FromBody] ChatLanguageUpdateDto request, CancellationToken cancellationToken)
    {
        return Room(chatRoomId, await service.UpdateRoomAsync(UserId(), chatRoomId, request.ToRequest(), cancellationToken));
    }

    [HttpDelete("/api/v1/chat/rooms/{chatRoomId}/language-settings")]
    public async Task<IActionResult> ResetRoom([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long chatRoomId, CancellationToken cancellationToken)
    {
        return Room(chatRoomId, await service.ResetRoomAsync(UserId(), chatRoomId, cancellationToken));
    }

    private IActionResult Default(ChatLanguageResult result)
    {
        return Ok(responses.Success(HttpContext, new ChatDefaultLanguageResponse(UserId(), result.Values.OriginalLanguageCode,
                result.Values.TranslationLanguageCode, result.Values.ShowOriginal, result.Values.ShowTranslation, result.Source)));
    }

    private IActionResult Room(long roomId, ChatLanguageResult result)
    {
        return Ok(responses.Success(HttpContext, new ChatRoomLanguageResponse(roomId, UserId(), result.Values.OriginalLanguageCode,
                result.Values.TranslationLanguageCode, result.Values.ShowOriginal, result.Values.ShowTranslation,
                result.RoomLanguageSettingApplied, result.Source)));
    }

    private long UserId()
    {
        return ChatReadAuthorization.TryGetUserId(User, out var userId) ? userId : throw new UnauthorizedAccessException();
    }
}

public sealed class ChatLanguageHttpBoundaryAttribute : ActionFilterAttribute, IExceptionFilter
{
    public ChatLanguageHttpBoundaryAttribute()
    {
        Order = -3200;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            context.Result = Error(context.HttpContext, 500, "", "언어 설정 요청 형식이 올바르지 않습니다.");
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
            ChatLanguageException business => (400, business.Code, business.Message),
            ChatLanguageDependencyUnavailableException or ChatMessageDependencyUnavailableException
                => (503, "CHAT_LANGUAGE_UNAVAILABLE", "언어 설정 의존성이 구성되지 않았습니다."),
            _ => (500, "", "언어 설정 요청을 처리하지 못했습니다.")
        };
        context.Result = Error(context.HttpContext, status, code, message);
        context.ExceptionHandled = true;
    }

    private static ObjectResult Error(HttpContext context, int status, string code, string message)
    {
        return new(context.RequestServices.GetRequiredService<ChatReadHttpResponses>()
                .Error(context, status, code, $"Message <{message}>"))
        {
            StatusCode = status
        };
    }
}

public sealed class ChatLanguageBooleanConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Jackson Boolean wrapper의 null/string/integer coercion을 보존한다. 소수와 임의 문자열은 오류다.
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
        {
            return reader.GetBoolean();
        }
        if (reader.TokenType == JsonTokenType.Number)
        {
            using var value = JsonDocument.ParseValue(ref reader);
            var number = value.RootElement.GetRawText();
            if (number.Contains('.') || number.Contains('e') || number.Contains('E'))
            {
                throw new JsonException();
            }
            return number.Any(character => character is >= '1' and <= '9');
        }
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString()!;
            int first = 0, last = value.Length - 1;
            while (first <= last && value[first] <= '\u0020')
            {
                first++;
            }
            while (last >= first && value[last] <= '\u0020')
            {
                last--;
            }
            return value[first..(last + 1)] switch
            {
                "" or "null" => null,
                "true" or "True" or "TRUE" => true,
                "false" or "False" or "FALSE" => false,
                _ => throw new JsonException()
            };
        }
        throw new JsonException();
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteBooleanValue(value.Value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

public static class ChatLanguageHttpExtensions
{
    public static IServiceCollection AddChatLanguageHttp(this IServiceCollection services)
    {
        services.TryAddScoped<IChatLanguageStore>(provider => new EfChatLanguageStore(
            provider.GetService<IDbContextFactory<ChatDbContext>>() ?? throw new ChatLanguageDependencyUnavailableException(),
            provider.GetService<IChatMessageProfileReader>(),
            () => provider.GetRequiredService<ChatReadLocalTime>().FromUtc(provider.GetRequiredService<TimeProvider>().GetUtcNow())));
        services.TryAddScoped<ChatLanguageService>();
        services.TryAddScoped<ChatLegacyMemberLanguageService>();
        return services;
    }
}
