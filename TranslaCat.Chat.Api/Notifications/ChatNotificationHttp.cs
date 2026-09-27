using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.Notifications;

namespace TranslaCat.Chat.Api.Notifications;

public sealed record ChatNotificationActivityDto(long Id, string NotificationType, long? RoomId, JsonElement Payload,
    [property: JsonPropertyName("isRead")] bool IsRead, string? ReadAt, string CreatedAt);
public sealed record ChatNotificationActivityPageDto(IReadOnlyList<ChatNotificationActivityDto> Items, long? NextCursorId, bool HasNext);
public sealed record ChatNotificationLatestMessageDto(long Id, string? SenderDisplayName, string MessageType, string? ContentPreview, string CreatedAt);
public sealed record ChatNotificationChatItemDto(long RoomId, string RoomType, string SourceType, string? RoomDisplayName,
    string? RoomAvatarUrl, ChatNotificationLatestMessageDto LatestMessage, long UnreadCount, long FirstUnreadMessageId);
public sealed record ChatNotificationChatPageDto(IReadOnlyList<ChatNotificationChatItemDto> Items, long? NextCursorMessageId, bool HasNext);

public sealed class ChatNotificationContractMapper(ChatReadLocalTime localTime)
{
    private readonly ChatReadTimestampFormatter timestamps = new(localTime.SourceTimeZone);

    public ChatNotificationActivityDto ToResponse(ChatNotificationActivity value)
    {
        // 원본 mapper는 손상/비객체 payload를 빈 객체로 반환한다. 저장 원문이나 예외를 노출하지 않는다.
        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(value.PayloadJson);
            payload = document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.Clone() : JsonSerializer.SerializeToElement(new
                {
                });
        }
        catch (JsonException)
        {
            payload = JsonSerializer.SerializeToElement(new
            {
            });
        }
        return new(value.Id, value.NotificationType, value.RoomId, payload, value.IsRead,
            value.ReadAt is { } at ? timestamps.Format(at) : null, timestamps.Format(value.CreatedAt));
    }

    public ChatNotificationActivityPageDto ToResponse(ChatNotificationActivityPage value)
    {
        return new(value.Items.Select(ToResponse).ToArray(), value.NextCursorId, value.HasNext);
    }

    public object ToCreatedEvent(ChatNotificationActivity value, DateTime occurredAt)
    {
        return new
        {
            eventType = "chat.notification.created",
            notification = ToResponse(value),
            occurredAt = timestamps.Format(occurredAt)
        };
    }

    public ChatNotificationChatPageDto ToResponse(ChatNotificationChatPage value)
    {
        return new(value.Items.Select(item => new ChatNotificationChatItemDto(item.RoomId, item.RoomType, item.SourceType,
                item.RoomDisplayName, item.RoomAvatarUrl, new(item.LatestMessage.Id, item.LatestMessage.SenderDisplayName,
                    item.LatestMessage.MessageType, item.LatestMessage.ContentPreview, timestamps.Format(item.LatestMessage.CreatedAt)),
                item.UnreadCount, item.FirstUnreadMessageId)).ToArray(), value.NextCursorMessageId, value.HasNext);
    }
}

[ApiController, ChatReadEndpoint, ChatNotificationHttpBoundary, Authorize(Policy = ChatReadAuthorization.Policy)]
[Route("api/v1/chat/notifications")]
public sealed class ChatNotificationController(ChatNotificationService service, ChatNotificationContractMapper mapper, ChatReadHttpResponses responses) : ControllerBase
{
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
    {
        return Ok(responses.Success(HttpContext, await service.GetSummaryAsync(UserId(), cancellationToken)));
    }

    [HttpGet("activities")]
    public async Task<IActionResult> Activities(
        [FromQuery, ModelBinder(BinderType = typeof(ChatNotificationBooleanBinder))] bool? onlyUnread,
        [FromQuery, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long? cursorId,
        [FromQuery, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] int? size,
        CancellationToken cancellationToken)
    {
        return Ok(responses.Success(HttpContext, mapper.ToResponse(await service.GetActivitiesAsync(UserId(), onlyUnread == true, cursorId, size, cancellationToken))));
    }

    [HttpGet("chats")]
    public async Task<IActionResult> Chats(
        [FromQuery, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long? cursorMessageId,
        [FromQuery, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] int? size,
        CancellationToken cancellationToken)
    {
        return Ok(responses.Success(HttpContext, mapper.ToResponse(await service.GetUnreadChatsAsync(UserId(), cursorMessageId, size, cancellationToken))));
    }

    [HttpPatch("activities/{notificationId}/read")]
    public async Task<IActionResult> Read([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long notificationId, CancellationToken cancellationToken)
    {
        return Ok(responses.Success(HttpContext, mapper.ToResponse(await service.MarkReadAsync(UserId(), notificationId, cancellationToken))));
    }

    [HttpPatch("activities/read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken cancellationToken)
    {
        return Ok(responses.Success(HttpContext, new
        {
            updatedCount = await service.MarkAllReadAsync(UserId(), cancellationToken)
        }));
    }

    private long UserId()
    {
        return ChatReadAuthorization.TryGetUserId(User, out var userId) ? userId : throw new UnauthorizedAccessException();
    }
}

public sealed class ChatNotificationHttpBoundaryAttribute : ActionFilterAttribute, IExceptionFilter
{
    public ChatNotificationHttpBoundaryAttribute()
    {
        Order = -3200;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            context.Result = Error(context.HttpContext, 500, "", "채팅 알림 요청 형식이 올바르지 않습니다.");
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
            ChatNotificationException business => (400, business.Code, business.Message),
            ChatNotificationDependencyUnavailableException => (503, "CHAT_NOTIFICATION_UNAVAILABLE", "채팅 알림 의존성이 구성되지 않았습니다."),
            _ => (500, "", "채팅 알림 요청을 처리하지 못했습니다.")
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

public sealed class ChatNotificationBooleanBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext context)
    {
        var supplied = context.ValueProvider.GetValue(context.ModelName);
        if (supplied == ValueProviderResult.None)
        {
            return Task.CompletedTask;
        }
        context.ModelState.SetModelValue(context.ModelName, supplied);
        var value = supplied.FirstValue ?? "";
        int first = 0, last = value.Length - 1;
        while (first <= last && value[first] <= '\u0020')
        {
            first++;
        }
        while (last >= first && value[last] <= '\u0020')
        {
            last--;
        }
        var normalized = value[first..(last + 1)].ToLowerInvariant();
        // Spring query Boolean은 JSON Boolean과 달리 on/off/yes/no/1/0도 받는다.
        bool? result = normalized switch
        {
            "true" or "on" or "yes" or "1" => true,
            "false" or "off" or "no" or "0" or "" => false,
            _ => null
        };
        if (result is null)
        {
            context.ModelState.TryAddModelError(context.ModelName, "Invalid boolean input.");
            context.Result = ModelBindingResult.Failed();
        }
        else
        {
            context.Result = ModelBindingResult.Success(result);
        }
        return Task.CompletedTask;
    }
}

public static class ChatNotificationHttpExtensions
{
    public static IServiceCollection AddChatNotificationHttp(this IServiceCollection services)
    {
        services.TryAddScoped<IChatNotificationStore>(provider => new EfChatNotificationStore(
            provider.GetService<IDbContextFactory<ChatDbContext>>() ?? throw new ChatNotificationDependencyUnavailableException()));
        services.TryAddScoped(provider => new ChatNotificationService(provider.GetRequiredService<IChatNotificationStore>(),
            provider.GetService<IChatNotificationProfileReader>(),
            () => provider.GetRequiredService<ChatReadLocalTime>().FromUtc(provider.GetRequiredService<TimeProvider>().GetUtcNow())));
        services.TryAddSingleton<ChatNotificationContractMapper>();
        services.TryAddScoped<IChatActivityNotificationDelivery, ChatActivityNotificationRealtimeDelivery>();
        services.TryAddScoped<IChatActivityNotificationWriter>(provider => new EfChatActivityNotificationWriter(
            provider.GetService<IDbContextFactory<ChatDbContext>>() ?? throw new ChatNotificationDependencyUnavailableException(),
            provider.GetService<IChatActivityNotificationDelivery>(),
            () => provider.GetRequiredService<ChatReadLocalTime>().FromUtc(provider.GetRequiredService<TimeProvider>().GetUtcNow())));
        return services;
    }
}
