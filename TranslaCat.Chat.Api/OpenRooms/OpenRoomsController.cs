using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.OpenRooms;

namespace TranslaCat.Chat.Api.OpenRooms;

[ApiController, ChatReadEndpoint, OpenRoomHttpBoundary]
[Authorize(Policy = ChatReadAuthorization.Policy)]
[Route("api/v1/chat/open-rooms")]
public sealed class OpenRoomsController(OpenRoomService service, OpenRoomContractMapper mapper, ChatReadHttpResponses responses) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] OpenRoomCreateDto request, CancellationToken token)
    {
        var room = await service.CreateAsync(UserId(), new(request.Name, request.Description, request.Visibility,
            request.MaxMemberCount, request.OwnerProfile is null ? null : new(request.OwnerProfile.Nickname, request.OwnerProfile.ProfileImageObjectKey)), token);
        return StatusCode(201, responses.Success(HttpContext, mapper.Detail(room)) with
        {
            ResultCode = 201,
            Message = "Created"
        });
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? keyword,
        [ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long? cursorId,
        [ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] int? size, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.List(await service.ListAsync(UserId(), keyword, cursorId, size, token))));
    }

    [HttpGet("{roomId}")]
    public async Task<IActionResult> Detail([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.Detail(await service.GetDetailAsync(UserId(), roomId, token))));
    }

    [HttpGet("{roomId}/me/profile")]
    public async Task<IActionResult> MyProfile([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.Profile(await service.GetProfileAsync(UserId(), roomId, null, token))));
    }

    [HttpGet("{roomId}/members/{openChatMemberId}")]
    public async Task<IActionResult> Profile([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long openChatMemberId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.Profile(await service.GetProfileAsync(UserId(), roomId, openChatMemberId, token))));
    }

    [HttpGet("{roomId}/members")]
    public async Task<IActionResult> Members([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.Members(await service.GetMembersAsync(UserId(), roomId, token))));
    }

    [HttpPatch("{roomId}/me/profile")]
    public async Task<IActionResult> Update([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [FromBody] OpenProfileUpdateDto? request, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.Profile(await service.UpdateNicknameAsync(UserId(), roomId, request?.Nickname, token))));
    }

    [HttpDelete("{roomId}/me/profile-image")]
    public async Task<IActionResult> DeleteImage([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.Profile(await service.DeleteImageAsync(UserId(), roomId, token))));
    }

    private long UserId()
    {
        return ChatReadAuthorization.TryGetUserId(User, out long id) ? id : throw new UnauthorizedAccessException();
    }
}
