using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.Read;

namespace TranslaCat.Chat.Api.Controllers;

[ApiController]
[ChatReadEndpoint]
[ChatReadInputValidation]
[Authorize(Policy = ChatReadAuthorization.Policy)]
[Route("api/v1/chat/rooms/{chatRoomId}/read")]
public sealed class ChatRoomReadController(
    ChatRoomReadService service,
    ChatReadContractMapper mapper,
    ChatReadHttpResponses responses) : ControllerBase
{
    [HttpPatch]
    public async Task<ActionResult<ChatReadHttpResponse<ChatRoomReadResponseDto>>> MarkAsRead(
        [FromRoute] long chatRoomId,
        [FromBody] ChatRoomReadRequestDto request,
        CancellationToken cancellationToken)
    {
        // 인증 정책에서 확인한 내부 ID만 사용한다. room ID에는 원본에 없는 양수 제약을 추가하지 않는다.
        if (!ChatReadAuthorization.TryGetUserId(User, out var userId))
        {
            return StatusCode(403, responses.Error(HttpContext, 403, "ACCESS_DENIED", "Message <ACCESS_DENIED>"));
        }

        // 상태 변경과 이벤트 등록은 P2-A에 위임하고, HTTP는 별도 wire DTO로 매핑한다.
        var result = await service.MarkAsReadAsync(
            userId,
            chatRoomId,
            new ChatRoomReadRequest(request.LastReadMessageId),
            cancellationToken);

        return Ok(responses.Success(HttpContext, mapper.ToResponse(result)));
    }
}
