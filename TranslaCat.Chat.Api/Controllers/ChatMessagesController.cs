using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Api.Controllers;

[ApiController]
[ChatReadEndpoint]
[ChatMessageHttpBoundary]
[Authorize(Policy = ChatReadAuthorization.Policy)]
[Route("api/v1/chat/rooms/{chatRoomId}/messages")]
public sealed class ChatMessagesController(
    ChatMessageService service,
    ChatMessageContractMapper mapper,
    ChatReadHttpResponses responses) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetMessages(
        [FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long chatRoomId,
        [FromQuery, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long? cursorId,
        CancellationToken cancellationToken)
    {
        var page = await service.GetMessagesAsync(UserId(), chatRoomId, cursorId, cancellationToken);
        return Ok(responses.Success(HttpContext, mapper.ToResponse(page)));
    }

    [HttpGet("after")]
    public async Task<IActionResult> GetMessagesAfter(
        [FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long chatRoomId,
        [FromQuery, BindRequired, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long cursorId,
        [FromQuery, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] int? size,
        CancellationToken cancellationToken)
    {
        var page = await service.GetMessagesAfterAsync(UserId(), chatRoomId, cursorId, size, cancellationToken);
        return Ok(responses.Success(HttpContext, mapper.ToResponse(page)));
    }

    [HttpGet("anchor")]
    public async Task<IActionResult> GetMessagesAroundAnchor(
        [FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long chatRoomId,
        [FromQuery, BindRequired, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long anchorMessageId,
        [FromQuery, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] int? beforeSize,
        [FromQuery, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] int? afterSize,
        CancellationToken cancellationToken)
    {
        var page = await service.GetMessagesAroundAnchorAsync(UserId(), chatRoomId, anchorMessageId, beforeSize, afterSize, cancellationToken);
        return Ok(responses.Success(HttpContext, mapper.ToResponse(page)));
    }

    [HttpPost]
    public async Task<IActionResult> CreateText(
        [FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long chatRoomId,
        [FromBody] ChatMessageCreateRequestDto request,
        CancellationToken cancellationToken)
    {
        // 원본 Controller는 HTTP200을 반환하며 created는 envelope의 resultCode=201/Created를 의미한다.
        var message = await service.CreateTextAsync(UserId(), chatRoomId, request.Content, cancellationToken);
        var response = responses.Success(HttpContext, mapper.ToResponse(message)) with
        {
            ResultCode = 201,
            Message = "Created"
        };
        return Ok(response);
    }

    private long UserId()
    {
        return ChatReadAuthorization.TryGetUserId(User, out var userId) ? userId
                : throw new UnauthorizedAccessException("Authenticated identity required.");
    }
}
