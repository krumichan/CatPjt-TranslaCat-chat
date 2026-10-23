using TranslaCat.Chat.Application.Safety;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.NotificationPreferences;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.Notifications;

namespace TranslaCat.Chat.Api.NotificationPreferences;

[ApiController, ChatReadEndpoint, ChatNotificationPreferenceHttpBoundary, Authorize(Policy = ChatReadAuthorization.Policy)]
public sealed class ChatNotificationPreferenceController(ChatNotificationPreferenceService service, ChatReadHttpResponses responses) : ControllerBase
{
    [HttpGet("api/v1/chat/notifications/preferences")]
    public async Task<IActionResult> Get(CancellationToken token) =>
        Ok(responses.Success(HttpContext, await service.GetAsync(UserId(), token)));

    [HttpPut("api/v1/chat/notifications/preferences")]
    public async Task<IActionResult> Put([FromBody] JsonElement body, CancellationToken token) =>
        Ok(responses.Success(HttpContext, await service.PutAsync(UserId(), Boolean(body, "pushEnabled"), token)));

    [HttpGet("api/v1/chat/rooms/{roomId}/notification-preferences")]
    public async Task<IActionResult> Room([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId, CancellationToken token) =>
        Ok(responses.Success(HttpContext, await service.GetRoomAsync(UserId(), roomId, token)));

    [HttpPut("api/v1/chat/rooms/{roomId}/notification-preferences")]
    public async Task<IActionResult> PutRoom([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [FromBody] JsonElement body, CancellationToken token) =>
        Ok(responses.Success(HttpContext, await service.PutRoomAsync(UserId(), roomId, Boolean(body, "muted"), token)));

    private long UserId() => ChatReadAuthorization.TryGetUserId(User, out long id) && id > 0
        ? id : throw new UnauthorizedAccessException();

    private static bool Boolean(JsonElement body, string name)
    {
        // 누락·중복·문자열·계정/방 식별자 덧붙이기를 모두 거부한다. actor는 인증값만 사용한다.
        if (body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Count() != 1
            || !body.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ChatNotificationPreferenceException("CHAT_NOTIFICATION_PREFERENCE_INVALID_REQUEST");
        return value.GetBoolean();
    }
}

public sealed class ChatNotificationPreferenceHttpBoundaryAttribute : ActionFilterAttribute, IExceptionFilter
{
    public ChatNotificationPreferenceHttpBoundaryAttribute() { Order = -3200; }
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
            context.Result = Error(context.HttpContext, 400, "CHAT_NOTIFICATION_PREFERENCE_INVALID_REQUEST");
    }

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested) return;
        var (status, code) = context.Exception switch
        {
            ChatNotificationPreferenceException error => (400, error.Code),
            ChatNotificationPreferenceUnavailableException => (503, "CHAT_NOTIFICATION_PREFERENCE_UNAVAILABLE"),
            UnauthorizedAccessException => (401, "UNAUTHORIZED"),
            _ => (500, "CHAT_NOTIFICATION_PREFERENCE_FAILED")
        };
        context.Result = Error(context.HttpContext, status, code);
        context.ExceptionHandled = true;
    }

    private static ObjectResult Error(HttpContext context, int status, string code) => new(
        context.RequestServices.GetRequiredService<ChatReadHttpResponses>().Error(context, status, code,
            "알림 설정을 처리하지 못했습니다.")) { StatusCode = status };
}

public static class ChatNotificationPreferenceHttpExtensions
{
    public static IServiceCollection AddChatNotificationPreferenceHttp(this IServiceCollection services)
    {
        services.TryAddScoped<IChatNotificationPreferenceStore>(provider => new EfChatNotificationPreferenceStore(
            provider.GetService<IDbContextFactory<ChatDbContext>>() ?? throw new ChatNotificationPreferenceUnavailableException(), provider.GetService<IChatContentPolicy>()));
        services.TryAddScoped<ChatNotificationPreferenceService>();
        return services;
    }
}
