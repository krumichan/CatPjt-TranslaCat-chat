using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.Language;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Api.Language;

public sealed class ChatLegacyMemberLanguageDto : IValidatableObject
{
    [JsonConverter(typeof(ChatMessageContentConverter))]
    public string? OriginalLanguageCode
    {
        get; init;
    }
    [JsonConverter(typeof(ChatMessageContentConverter))]
    public string? TranslationLanguageCode
    {
        get; init;
    }
    [JsonConverter(typeof(ChatLanguageBooleanConverter))]
    public bool? ShowOriginal
    {
        get; init;
    }
    [JsonConverter(typeof(ChatLanguageBooleanConverter))]
    public bool? ShowTranslation
    {
        get; init;
    }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // 기존 @NotBlank/@Size는 원문 길이 10과 Java trim을 사용한다. 모델 검증 오류는 기존 500 경로다.
        foreach (var (name, value) in new[] { (nameof(OriginalLanguageCode), OriginalLanguageCode), (nameof(TranslationLanguageCode), TranslationLanguageCode) })
        {
            if (value is null || ChatMessageText.Trim(value).Length == 0 || value.Length > 10)
            {
                yield return new ValidationResult("Language code is required and must have at most ten characters.", [name]);
            }
        }
    }

    public ChatLegacyLanguageUpdate ToRequest()
    {
        return new(OriginalLanguageCode!, TranslationLanguageCode!, ShowOriginal ?? false, ShowTranslation ?? false);
    }
}

[ApiController, ChatReadEndpoint, ChatLanguageHttpBoundary, Authorize(Policy = ChatReadAuthorization.Policy)]
public sealed class ChatLegacyMemberLanguageController(ChatLegacyMemberLanguageService service, ChatReadHttpResponses responses) : ControllerBase
{
    [HttpGet("/api/v1/chat/rooms/{chatRoomId}/members/me/language")]
    public async Task<IActionResult> Get([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long chatRoomId, CancellationToken cancellationToken)
    {
        return LegacyResponse(chatRoomId, await service.GetAsync(UserId(), chatRoomId, cancellationToken));
    }

    [HttpPatch("/api/v1/chat/rooms/{chatRoomId}/members/me/language")]
    public async Task<IActionResult> Update([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long chatRoomId,
        [FromBody] ChatLegacyMemberLanguageDto request, CancellationToken cancellationToken)
    {
        return LegacyResponse(chatRoomId, await service.UpdateAsync(UserId(), chatRoomId, request.ToRequest(), cancellationToken));
    }

    [HttpDelete("/api/v1/chat/rooms/{chatRoomId}/members/me/language")]
    public async Task<IActionResult> Reset([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long chatRoomId, CancellationToken cancellationToken)
    {
        return LegacyResponse(chatRoomId, await service.ResetAsync(UserId(), chatRoomId, cancellationToken));
    }

    private IActionResult LegacyResponse(long roomId, ChatLanguageResult result)
    {
        return Ok(responses.Success(HttpContext, new
        {
            chatRoomId = roomId,
            userId = UserId(),
            result.Values.OriginalLanguageCode,
            result.Values.TranslationLanguageCode,
            result.Values.ShowOriginal,
            result.Values.ShowTranslation,
            result.RoomLanguageSettingApplied
        }));
    }

    private long UserId()
    {
        return ChatReadAuthorization.TryGetUserId(User, out var userId) ? userId : throw new UnauthorizedAccessException();
    }
}
