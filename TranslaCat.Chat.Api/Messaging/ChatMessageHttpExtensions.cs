using Microsoft.Extensions.DependencyInjection.Extensions;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Api.Messaging;

public static class ChatMessageHttpExtensions
{
    public static IServiceCollection AddChatMessageHttp(this IServiceCollection services)
    {
        // 공통 Controller/auth/envelope은 AddChatReadHttp가 제공한다. 실제 transaction은 composition root가 연결한다.
        services.TryAddSingleton(provider => new ChatMessageContractMapper(
            provider.GetRequiredService<ChatReadLocalTime>().SourceTimeZone));
        services.TryAddScoped(provider => new ChatMessageService(
            provider.GetService<IChatMessageTransaction>() ?? throw new ChatMessageDependencyUnavailableException("message transaction"),
            () => provider.GetRequiredService<ChatReadLocalTime>().FromUtc(provider.GetRequiredService<TimeProvider>().GetUtcNow())));
        return services;
    }
}
