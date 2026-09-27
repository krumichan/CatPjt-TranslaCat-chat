using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.Translation;

namespace TranslaCat.Chat.Api.Translation;

[ApiController]
[ChatReadEndpoint]
[ChatMessageHttpBoundary]
[Authorize(Policy = ChatReadAuthorization.Policy)]
[Route("api/v1/chat/rooms/{chatRoomId}/messages/{messageId}/translations/{languageCode}/retry")]
public sealed class ChatTranslationController(
    ChatTranslationRetryService service,
    ChatTranslationContractMapper mapper,
    ChatReadHttpResponses responses) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Retry(
        [FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long chatRoomId,
        [FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long messageId,
        [FromRoute] string languageCode,
        CancellationToken cancellationToken)
    {
        if (!ChatReadAuthorization.TryGetUserId(User, out long userId))
        {
            throw new UnauthorizedAccessException("Authenticated identity required.");
        }

        var result = await service.RetryAsync(userId, chatRoomId, messageId, languageCode, cancellationToken);
        return Ok(responses.Success(HttpContext, mapper.ToResponse(result)));
    }
}
