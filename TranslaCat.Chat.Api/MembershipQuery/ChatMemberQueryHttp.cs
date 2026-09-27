using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TranslaCat.Chat.Api.Membership;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.MembershipQuery;
using TranslaCat.Chat.Application.Presence;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.MembershipQuery;

namespace TranslaCat.Chat.Api.MembershipQuery;

[ApiController, ChatReadEndpoint, ChatMembershipHttpBoundary, Authorize(Policy = ChatReadAuthorization.Policy)]
public sealed class ChatMemberQueryController(ChatMemberQueryService service, ChatReadHttpResponses responses, ChatReadLocalTime localTime) : ControllerBase
{
    [HttpGet("/api/v1/chat/rooms/{chatRoomId}/members")]
    public async Task<IActionResult> List([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long chatRoomId, CancellationToken cancellationToken)
    {
        var value = await service.ListAsync(UserId(), chatRoomId, cancellationToken);
        var time = new ChatReadTimestampFormatter(localTime.SourceTimeZone);
        return Ok(responses.Success(HttpContext, new
        {
            members = value.Members.Select(member => new
            {
                member.State.Id,
                member.State.ChatRoomId,
                member.State.UserId,
                member.Profile.PublicId,
                displayName = member.Profile.Nickname,
                member.Profile.ProfileImageUrl,
                member.State.Role,
                member.State.Active,
                member.Online,
                joinedAt = time.Format(member.State.JoinedAt),
                leftAt = member.State.LeftAt is { } left ? time.Format(left) : null
            }).ToArray(),
            aiMembers = value.AiMembers.Select(member => new
            {
                member.AiMemberId,
                member.Nickname,
                member.ProfileImageUrl,
                member.Role,
                member.Active,
                joinedAt = time.Format(member.JoinedAt)
            }).ToArray(),
            value.AiDisclosureType
        }));
    }

    [HttpGet("/api/v1/chat/rooms/{chatRoomId}/members/{targetUserId}/profile")]
    public async Task<IActionResult> Profile([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long chatRoomId,
        [FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long targetUserId, CancellationToken cancellationToken)
    {
        var value = await service.ProfileAsync(UserId(), chatRoomId, targetUserId, cancellationToken);
        return Ok(responses.Success(HttpContext, new
        {
            value.Profile.UserId,
            value.Profile.PublicId,
            displayName = value.Profile.Nickname,
            value.Profile.ProfileImageUrl,
            value.Profile.ProfileBackgroundImageUrl,
            value.Profile.Bio,
            value.FriendStatus,
            value.Online
        }));
    }

    private long UserId()
    {
        return ChatReadAuthorization.TryGetUserId(User, out var userId) ? userId : throw new UnauthorizedAccessException();
    }
}

public static class ChatMemberQueryExtensions
{
    public static IServiceCollection AddChatMemberQueryHttp(this IServiceCollection services)
    {
        services.TryAddScoped<IChatMemberQueryStore>(provider => new EfChatMemberQueryStore(
            provider.GetService<IDbContextFactory<ChatDbContext>>() ?? throw new ChatMembershipDependencyUnavailableException(),
            provider.GetService<IChatAiProfileStorage>()));
        services.TryAddScoped(provider => new ChatMemberQueryService(provider.GetRequiredService<IChatMemberQueryStore>(),
            provider.GetService<IChatMemberProfileReader>(), provider.GetService<ChatPresenceCoordinator>()));
        return services;
    }
}
