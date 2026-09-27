using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Runtime;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.Presence;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.OpenRooms;

namespace TranslaCat.Chat.Api.OpenRooms;

public static class OpenRoomExtensions
{
    public static IServiceCollection AddOpenRoomHttp(this IServiceCollection services)
    {
        services.TryAddSingleton<OpenRoomContractMapper>();
        services.TryAddScoped(provider => new OpenRoomService(
            provider.GetService<IOpenRoomStore>() ?? throw new OpenRoomDependencyUnavailableException(),
            () => provider.GetRequiredService<ChatReadLocalTime>().FromUtc(provider.GetRequiredService<TimeProvider>().GetUtcNow())));
        return services;
    }

    public static IServiceCollection AddOpenRoomPersistence(this IServiceCollection services)
    {
        services.TryAddScoped<IOpenProfileEventDelivery, OpenProfileRealtimeDelivery>();
        services.TryAddScoped(provider => new EfOpenRoomStore(provider.GetRequiredService<IDbContextFactory<ChatDbContext>>(),
            provider.GetRequiredService<ILogger<EfOpenRoomStore>>(), provider.GetService<IChatRoomAccountReader>(),
            provider.GetService<IOpenProfileStorage>(), provider.GetService<IOpenProfileEventDelivery>(),
            provider.GetService<ChatPresenceCoordinator>()));
        services.TryAddScoped<IOpenRoomStore>(provider => provider.GetRequiredService<EfOpenRoomStore>());
        return services;
    }
}

public sealed class OpenProfileRealtimeDelivery(OpenRoomContractMapper mapper, IServiceProvider services) : IOpenProfileEventDelivery
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool IsConfigured => services.GetService<ChatRealtimeRedisRelay>() is not null;

    public Task DeliverAsync(OpenProfileChanged change, CancellationToken cancellationToken)
    {
        return (services.GetService<ChatRealtimeRedisRelay>() ?? throw new OpenRoomDependencyUnavailableException())
            .PublishRoomAsync(change.RoomId, JsonSerializer.Serialize(mapper.Event(change), Json), cancellationToken);
    }
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class OpenRoomHttpBoundaryAttribute : ActionFilterAttribute, IExceptionFilter
{
    public OpenRoomHttpBoundaryAttribute()
    {
        Order = -3200;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            context.Result = Error(context.HttpContext, 500, "", "OPEN 채팅방 요청 형식 또는 입력 값이 올바르지 않습니다.");
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
            OpenRoomException business => (400, business.Code, business.Message),
            OpenRoomDependencyUnavailableException or ChatRoomDependencyUnavailableException =>
                (503, "OPEN_CHAT_UNAVAILABLE", "OPEN 채팅 의존성이 구성되지 않았습니다."),
            _ => (500, "", "OPEN 채팅 요청을 처리하지 못했습니다.")
        };
        context.Result = Error(context.HttpContext, status, code, message);
        context.ExceptionHandled = true;
    }

    private static ObjectResult Error(HttpContext context, int status, string code, string message)
    {
        return new(context.RequestServices.GetRequiredService<ChatReadHttpResponses>()
            .Error(context, status, code, $"Message <{message}>"))
        {
            StatusCode = status
        };
    }
}
