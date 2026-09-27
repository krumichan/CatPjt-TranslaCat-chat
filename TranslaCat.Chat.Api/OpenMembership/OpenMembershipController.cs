using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.OpenRooms;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.OpenMembership;

namespace TranslaCat.Chat.Api.OpenMembership;

public sealed class OpenJoinDto
{
    public OpenOwnerProfileDto? Profile
    {
        get; init;
    }
}

public sealed class OpenOwnerTransferDto
{
    [JsonConverter(typeof(ChatReadMessageIdConverter))]
    public long? TargetOpenChatMemberId
    {
        get; init;
    }
}

[ApiController, ChatReadEndpoint, OpenRoomHttpBoundary]
[Authorize(Policy = ChatReadAuthorization.Policy)]
[Route("api/v1/chat/open-rooms/{roomId}")]
public sealed class OpenMembershipController(OpenMembershipService service, OpenRoomContractMapper mapper, ChatReadHttpResponses responses) : ControllerBase
{
    [HttpPost("join")]
    public async Task<IActionResult> Join([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] OpenJoinDto? request, CancellationToken token)
    {
        var profile = request?.Profile;
        var result = await service.JoinAsync(UserId(), roomId,
            profile is null ? null : new(profile.Nickname, profile.ProfileImageObjectKey), token);
        return Ok(responses.Success(HttpContext, mapper.Detail(result)));
    }

    [HttpDelete("leave")]
    public async Task<IActionResult> Leave([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId, CancellationToken token)
    {
        var result = await service.LeaveAsync(UserId(), roomId, token);
        return Ok(responses.Success(HttpContext, new
        {
            result.RoomId,
            result.Joined,
            result.MyRole,
            myOpenProfile = mapper.Profile(result.MyOpenProfile)
        }));
    }

    [HttpPost("owner-transfer")]
    public async Task<IActionResult> Transfer([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [FromBody] OpenOwnerTransferDto? request, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.Detail(await service.TransferAsync(UserId(), roomId, request?.TargetOpenChatMemberId, token))));
    }

    [HttpPost("close")]
    public async Task<IActionResult> Close([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.Detail(await service.CloseAsync(UserId(), roomId, token))));
    }

    private long UserId()
    {
        return ChatReadAuthorization.TryGetUserId(User, out long id) ? id : throw new UnauthorizedAccessException();
    }
}
