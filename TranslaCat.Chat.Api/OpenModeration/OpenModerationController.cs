using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.OpenRooms;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.OpenModeration;

namespace TranslaCat.Chat.Api.OpenModeration;

public sealed class OpenBanDto : IValidatableObject
{
    [JsonConverter(typeof(ChatReadMessageIdConverter))]
    public long? TargetOpenChatMemberId
    {
        get; init;
    }
    [JsonConverter(typeof(ChatMessageContentConverter))]
    public string? Reason
    {
        get; init;
    }
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TargetOpenChatMemberId is null or <= 0 || ChatMessageText.IsBlank(Reason) || Reason?.Length > 500)
        {
            yield return new ValidationResult("Invalid OPEN ban request.");
        }
    }
}

[ApiController, ChatReadEndpoint, OpenRoomHttpBoundary]
[Authorize(Policy = ChatReadAuthorization.Policy)]
[Route("api/v1/chat/open-rooms/{roomId}")]
public sealed class OpenModerationController(OpenModerationService service, OpenRoomContractMapper profiles,
    OpenModerationMapper mapper, ChatReadHttpResponses responses) : ControllerBase
{
    [HttpPost("admins/{openChatMemberId}")]
    public async Task<IActionResult> Assign([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long openChatMemberId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, profiles.Profile(await service.ChangeAdminAsync(UserId(), roomId, openChatMemberId, true, token))));
    }

    [HttpDelete("admins/{openChatMemberId}")]
    public async Task<IActionResult> Revoke([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long openChatMemberId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, profiles.Profile(await service.ChangeAdminAsync(UserId(), roomId, openChatMemberId, false, token))));
    }

    [HttpPost("bans")]
    public async Task<IActionResult> Ban([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [FromBody] OpenBanDto request, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.Action(await service.BanAsync(UserId(), roomId,
            new(request.TargetOpenChatMemberId, request.Reason), token))));
    }

    [HttpPatch("bans/{banId}/release")]
    public async Task<IActionResult> Release([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long banId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.Action(await service.ReleaseAsync(UserId(), roomId, banId, token))));
    }

    [HttpGet("bans")]
    public async Task<IActionResult> List([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [FromQuery] string? keyword, [ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long? cursor,
        [ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] int? size, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.List(await service.ListAsync(UserId(), roomId, keyword, cursor, size, token))));
    }

    private long UserId()
    {
        return ChatReadAuthorization.TryGetUserId(User, out long id) ? id : throw new UnauthorizedAccessException();
    }
}

public sealed class OpenModerationMapper(ChatReadLocalTime localTime)
{
    private readonly ChatReadTimestampFormatter timestamps = new(localTime.SourceTimeZone);
    public object Action(OpenBanAction value)
    {
        return new
        {
            value.RoomId,
            value.BanId,
            value.TargetOpenChatMemberId,
            value.Active,
            bannedAt = timestamps.Format(value.BannedAt),
            releasedAt = value.ReleasedAt is null ? null : timestamps.Format(value.ReleasedAt.Value)
        };
    }

    public object List(OpenBanList value)
    {
        return new
        {
            items = value.Items.Select(item => new
            {
                item.BanId,
                item.TargetOpenChatMemberId,
                item.MemberCode,
                item.Nickname,
                item.ProfileImageUrl,
                lastJoinedAt = timestamps.Format(item.LastJoinedAt),
                bannedAt = timestamps.Format(item.BannedAt),
                item.BannedBy,
                item.Reason,
                item.Releasable
            }).ToArray(),
            value.NextCursorId,
            value.HasNext
        };
    }
}
