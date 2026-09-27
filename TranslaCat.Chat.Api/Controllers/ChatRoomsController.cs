using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Rooms;
using TranslaCat.Chat.Application.Rooms;

namespace TranslaCat.Chat.Api.Controllers;

[ApiController, ChatReadEndpoint, ChatRoomHttpBoundary]
[Authorize(Policy = ChatReadAuthorization.Policy)]
[Route("api/v1/chat/rooms")]
public sealed class ChatRoomsController(ChatRoomService service, ChatRoomContractMapper mapper, ChatReadHttpResponses responses) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ChatRoomCreateDto request, CancellationToken cancellationToken)
    {
        // 일반 생성만 연결한다. 친구·OPEN 생성/초대 권한을 이 endpoint로 우회하지 않는다.
        var room = await service.CreateAsync(UserId(), new(request.RoomType, request.Name, request.Description, request.MemberUserIds), cancellationToken);
        return StatusCode(201, responses.Success(HttpContext, mapper.Detail(room)) with
        {
            ResultCode = 201,
            Message = "Created"
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        return Ok(responses.Success(HttpContext, mapper.List(await service.ListAsync(UserId(), cancellationToken))));
    }

    [HttpGet("{roomId}")]
    public async Task<IActionResult> Get([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId, CancellationToken cancellationToken)
    {
        return Ok(responses.Success(HttpContext, mapper.Detail(await service.GetAsync(UserId(), roomId, cancellationToken))));
    }

    private long UserId()
    {
        return ChatReadAuthorization.TryGetUserId(User, out var userId) ? userId : throw new UnauthorizedAccessException();
    }
}
