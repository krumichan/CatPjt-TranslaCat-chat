using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Api.Rooms;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.Membership;

namespace TranslaCat.Chat.Api.Membership;

public sealed class ChatInvitationDto
{
    [JsonConverter(typeof(ChatRoomMemberIdsConverter))]
    public IReadOnlyList<long?>? TargetUserIds
    {
        get; init;
    }
    [JsonConverter(typeof(ChatInvitationPublicIdsConverter))]
    public IReadOnlyList<string?>? TargetPublicIds
    {
        get; init;
    }
    public ChatMembershipTargets Targets()
    {
        return new(TargetUserIds, TargetPublicIds);
    }
}

public sealed class ChatConversionDto
{
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
    [JsonConverter(typeof(ChatRoomMemberIdsConverter))]
    public IReadOnlyList<long?>? TargetUserIds
    {
        get; init;
    }
    [JsonConverter(typeof(ChatInvitationPublicIdsConverter))]
    public IReadOnlyList<string?>? TargetPublicIds
    {
        get; init;
    }
}

public sealed class ChatFriendGroupDto
{
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
    [JsonConverter(typeof(ChatRoomMemberIdsConverter))]
    public IReadOnlyList<long?>? MemberUserIds
    {
        get; init;
    }
}

public sealed class ChatInvitationPublicIdsConverter : JsonConverter<IReadOnlyList<string?>>
{
    public override IReadOnlyList<string?> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException();
        }
        var converter = new ChatMessageContentConverter();
        var values = new List<string?>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(reader.TokenType == JsonTokenType.Null ? null : converter.Read(ref reader, typeof(string), options));
        }
        if (reader.TokenType != JsonTokenType.EndArray)
        {
            throw new JsonException();
        }
        return values;
    }
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string?> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}

[ApiController, ChatReadEndpoint, ChatMembershipHttpBoundary, Authorize(Policy = ChatReadAuthorization.Policy)]
public sealed class ChatMembershipController(ChatMembershipService service, ChatReadHttpResponses responses, ChatReadLocalTime localTime) : ControllerBase
{
    [HttpPost("/api/v1/chat/friends/{friendUserId}/direct-room")]
    public async Task<IActionResult> FriendDirect([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long friendUserId,
        CancellationToken cancellationToken)
    {
        var roomId = await service.CreateOrGetFriendDirectAsync(UserId(), friendUserId, cancellationToken);
        return await RoomResponse(roomId, cancellationToken);
    }

    [HttpPost("/api/v1/chat/rooms/{chatRoomId}/members/invitations")]
    public async Task<IActionResult> Invite([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long chatRoomId,
        [FromBody] ChatInvitationDto request, CancellationToken cancellationToken)
    {
        return CreatedResponse(await service.InviteAsync(UserId(), chatRoomId, request.Targets(), cancellationToken));
    }

    [HttpPost("/api/v1/chat/rooms/{chatRoomId}/group-conversion")]
    public async Task<IActionResult> Convert([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long chatRoomId,
        [FromBody] ChatConversionDto request, CancellationToken cancellationToken)
    {
        return CreatedResponse(await service.ConvertAsync(UserId(), chatRoomId,
                new(request.Name, request.Description, new(request.TargetUserIds, request.TargetPublicIds)), cancellationToken));
    }

    [HttpPost("/api/v1/chat/friends/group-rooms")]
    public async Task<IActionResult> FriendGroup([FromBody] ChatFriendGroupDto request, CancellationToken cancellationToken)
    {
        var roomId = await service.CreateFriendGroupAsync(UserId(), new(request.Name, request.Description, request.MemberUserIds), cancellationToken);

        return await RoomResponse(roomId, cancellationToken);
    }

    private async Task<IActionResult> RoomResponse(long roomId, CancellationToken cancellationToken)
    {
        // 원본 facade처럼 생성/재사용 후 방 상세를 재조회한다. 일반 방 DTO/언어 정책을 복제하지 않는다.
        var roomService = HttpContext.RequestServices.GetRequiredService<ChatRoomService>();
        var mapper = HttpContext.RequestServices.GetRequiredService<ChatRoomContractMapper>();
        return Ok(responses.Success(HttpContext, mapper.Detail(await roomService.GetAsync(UserId(), roomId, cancellationToken))));
    }

    private IActionResult CreatedResponse(ChatInvitationResult result)
    {
        var timestamps = new ChatReadTimestampFormatter(localTime.SourceTimeZone);
        var body = new
        {
            result.RoomId,
            result.CreatedNewGroupRoom,
            invitedMembers = result.InvitedMembers.Select(member => new
            {
                member.UserId,
                member.PublicId,
                member.DisplayName,
                member.ProfileImageUrl,
                joinedAt = timestamps.Format(member.JoinedAt)
            }).ToArray()
        };
        return StatusCode(201, responses.Success(HttpContext, body) with
        {
            ResultCode = 201,
            Message = "Created"
        });
    }

    private long UserId()
    {
        return ChatReadAuthorization.TryGetUserId(User, out var userId) ? userId : throw new UnauthorizedAccessException();
    }
}

public sealed class ChatMembershipHttpBoundaryAttribute : ActionFilterAttribute, IExceptionFilter
{
    public ChatMembershipHttpBoundaryAttribute()
    {
        Order = -3200;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            context.Result = Error(context.HttpContext, 500, "", "멤버 요청 형식이 올바르지 않습니다.");
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
            ChatMembershipException business => (400, business.Code, business.Message),
            ChatRoomException business => (400, business.Code, business.Message),
            ChatMembershipDependencyUnavailableException or ChatRoomDependencyUnavailableException
                or ChatMessageDependencyUnavailableException or ChatNotificationDependencyUnavailableException
                => (503, "CHAT_MEMBERSHIP_UNAVAILABLE", "멤버 처리 의존성이 구성되지 않았습니다."),
            _ => (500, "", "멤버 요청을 처리하지 못했습니다.")
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

public static class ChatMembershipHttpExtensions
{
    public static IServiceCollection AddChatMembershipHttp(this IServiceCollection services)
    {
        services.TryAddScoped<IChatMembershipEventDelivery, ChatMembershipRealtimeDelivery>();
        services.TryAddScoped<IChatMembershipStore>(provider => new EfChatMembershipStore(
            provider.GetService<IDbContextFactory<ChatDbContext>>() ?? throw new ChatMembershipDependencyUnavailableException(),
            provider.GetService<IChatMembershipEventDelivery>(), provider.GetRequiredService<ILogger<EfChatMembershipStore>>()));
        services.TryAddScoped(provider => new ChatMembershipService(provider.GetRequiredService<IChatMembershipStore>(),
            provider.GetService<IChatMembershipDirectory>(),
            () => provider.GetRequiredService<ChatReadLocalTime>().FromUtc(provider.GetRequiredService<TimeProvider>().GetUtcNow())));
        return services;
    }
}
