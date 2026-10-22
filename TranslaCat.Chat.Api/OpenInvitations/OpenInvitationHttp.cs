using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Notifications;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Application.OpenInvitations;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.OpenInvitations;

namespace TranslaCat.Chat.Api.OpenInvitations;

[ApiController, ChatReadEndpoint, OpenInvitationHttpBoundary, Authorize(Policy = ChatReadAuthorization.Policy)]
public sealed class OpenInvitationController(OpenInvitationService service, ChatNotificationContractMapper mapper,
    ChatReadTimestampFormatter timestamps, ChatReadHttpResponses responses) : ControllerBase
{
    [HttpPost("api/v1/chat/open-rooms/{roomId}/invitations")]
    public async Task<IActionResult> Create([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [FromBody] OpenInvitationCreate? request, CancellationToken token)
    {
        var result = await service.CreateAsync(UserId(), roomId, request ?? new(null, null), token);
        return Ok(responses.Success(HttpContext, new { result.InvitationId, result.RoomId,
            createdAt = timestamps.Format(result.CreatedAt), result.Duplicate }));
    }

    [HttpGet("api/v1/chat/open-invitations")]
    public async Task<IActionResult> List([FromQuery, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long? cursorId,
        [FromQuery, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] int? size, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.ToResponse(await service.ListAsync(UserId(), cursorId, size, token))));
    }

    [HttpGet("api/v1/chat/open-invitations/summary")]
    public async Task<IActionResult> Summary(CancellationToken token)
    {
        var result = await service.SummaryAsync(UserId(), token);
        return Ok(responses.Success(HttpContext, new { result.UnreadCount,
            latestInvitation = result.LatestInvitation is { } latest ? mapper.ToResponse(latest) : null }));
    }

    [HttpGet("api/v1/chat/open-invitations/{invitationId}")]
    public async Task<IActionResult> Get([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long invitationId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.ToResponse(await service.GetAsync(UserId(), invitationId, token))));
    }

    [HttpPatch("api/v1/chat/open-invitations/{invitationId}/read")]
    public async Task<IActionResult> Read([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long invitationId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.ToResponse(await service.ReadAsync(UserId(), invitationId, token))));
    }

    private long UserId() => ChatReadAuthorization.TryGetUserId(User, out long id) ? id : throw new UnauthorizedAccessException();
}

public sealed class OpenInvitationHttpBoundaryAttribute : ActionFilterAttribute, IExceptionFilter
{
    public OpenInvitationHttpBoundaryAttribute() { Order = -3200; }
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
            context.Result = Error(context.HttpContext, 400, "OPEN_INVITATION_INVALID_REQUEST", "초대 요청 형식을 확인해 주세요.");
    }

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested) return;
        var (status, code, message) = context.Exception switch
        {
            OpenInvitationException business => (400, business.Code, business.Message),
            OpenInvitationDependencyUnavailableException or ChatCoreUnavailableException or ChatNotificationDependencyUnavailableException =>
                (503, "OPEN_INVITATION_UNAVAILABLE", "초대 서비스에 연결하지 못했습니다."),
            _ => (500, "", "초대를 처리하지 못했습니다.")
        };
        context.Result = Error(context.HttpContext, status, code, message);
        context.ExceptionHandled = true;
    }

    private static ObjectResult Error(HttpContext context, int status, string code, string message) => new(
        context.RequestServices.GetRequiredService<ChatReadHttpResponses>().Error(context, status, code, message)) { StatusCode = status };
}

public static class OpenInvitationHttpExtensions
{
    public static IServiceCollection AddOpenInvitationHttp(this IServiceCollection services)
    {
        services.TryAddScoped<IOpenInvitationStore>(provider => new EfOpenInvitationStore(
            provider.GetService<IDbContextFactory<ChatDbContext>>() ?? throw new OpenInvitationDependencyUnavailableException(),
            provider.GetService<IChatMembershipDirectory>() ?? throw new OpenInvitationDependencyUnavailableException(),
            provider.GetService<IChatActivityNotificationDelivery>() ?? throw new OpenInvitationDependencyUnavailableException(),
            provider.GetRequiredService<ILogger<EfOpenInvitationStore>>(), () =>
            {
                var instant = provider.GetRequiredService<TimeProvider>().GetUtcNow();
                return (provider.GetRequiredService<ChatReadLocalTime>().FromUtc(instant), instant.UtcDateTime);
            }));
        services.TryAddScoped(provider => new OpenInvitationService(provider.GetRequiredService<IOpenInvitationStore>(),
            () => provider.GetRequiredService<ChatReadLocalTime>().FromUtc(provider.GetRequiredService<TimeProvider>().GetUtcNow())));
        services.TryAddSingleton(provider => new ChatReadTimestampFormatter(provider.GetRequiredService<ChatReadLocalTime>().SourceTimeZone));
        return services;
    }
}
