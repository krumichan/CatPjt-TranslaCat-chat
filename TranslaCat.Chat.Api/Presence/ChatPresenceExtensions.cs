using Microsoft.Extensions.DependencyInjection.Extensions;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.Presence;
using TranslaCat.Chat.Application.Realtime;
using TranslaCat.Chat.Infrastructure.Persistence.Repositories;

namespace TranslaCat.Chat.Api.Presence;

public static class ChatPresenceExtensions
{
    public static IServiceCollection AddChatPresenceFanout(this IServiceCollection services)
    {
        services.TryAddScoped<IChatPresenceRoomReader, EfChatPresenceRoomReader>();
        services.TryAddSingleton(provider => new ChatReadTimestampFormatter(
            provider.GetRequiredService<ChatReadLocalTime>().SourceTimeZone));
        services.TryAddScoped<ChatPresenceFanout>();
        return services;
    }

    public static IServiceCollection AddChatPresenceRuntime(this IServiceCollection services)
    {
        // 실제 store/publisher/coordinator 설정은 composition root가 공급한다. 운영 fake는 등록하지 않는다.
        services.TryAddSingleton<IChatRealtimeSessionLifecycle, ChatPresenceSessionLifecycle>();
        services.AddHostedService<ChatPresenceHostedService>();
        return services;
    }
}
