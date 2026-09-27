using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TranslaCat.Chat.Api.Realtime;

public static class ChatRealtimeExtensions
{
    public static IServiceCollection AddChatRealtime(this IServiceCollection services)
    {
        services.TryAddSingleton<ChatRealtimeBroker>();
        services.TryAddSingleton<ChatRealtimeEndpoint>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ChatRealtimeOptions>();
        return services;
    }

    public static WebApplication MapChatRealtime(this WebApplication application)
    {
        var options = application.Services.GetRequiredService<ChatRealtimeOptions>();
        options.Validate();
        var webSockets = new WebSocketOptions { KeepAliveInterval = options.KeepAliveInterval };
        foreach (var origin in options.AllowedOrigins)
        {
            webSockets.AllowedOrigins.Add(origin);
        }
        application.UseWebSockets(webSockets);
        application.MapGet("/ws/chat", (HttpContext context, ChatRealtimeEndpoint endpoint) => endpoint.HandleAsync(context));
        return application;
    }
}
