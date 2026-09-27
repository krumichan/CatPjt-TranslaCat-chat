using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Runtime;
using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Infrastructure.Ai;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Translation;

namespace TranslaCat.Chat.Api.Ai;

public static class ChatAiExtensions
{
    public static IServiceCollection AddChatAi(this IServiceCollection services, ChatAiOptions options)
    {
        options.Validate();
        services.AddSingleton(options);
        services.TryAddSingleton<ChatExternalApiCircuitBreaker>();
        services.AddSingleton(provider => new ChatAiRuntime(provider.GetRequiredService<IServiceScopeFactory>(),
            options, provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IHostApplicationLifetime>(),
            () =>
            {
                var registered = provider.GetRequiredService<IServiceProviderIsService>();
                return registered.IsService(typeof(IDbContextFactory<ChatDbContext>))
                    && registered.IsService(typeof(ChatRealtimeRedisRelay)) && registered.IsService(typeof(IChatAiUserNameReader));
            }, provider.GetRequiredService<ILogger<ChatAiRuntime>>()));
        services.AddSingleton<IChatAiMessageDispatcher>(provider => provider.GetRequiredService<ChatAiRuntime>());
        services.AddSingleton<IChatAiDelay>(provider => provider.GetRequiredService<ChatAiRuntime>());
        services.AddHostedService(provider => provider.GetRequiredService<ChatAiRuntime>());

        // 기존 AI 업무 API만 호출한다. prompt/model/provider 변경이나 숨은 fake fallback은 없다.
        services.AddHttpClient<IChatAiReplyClient, HttpChatAiReplyClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectTimeout = options.ConnectTimeout,
                AllowAutoRedirect = false
            })
            .RedactLoggedHeaders(["X-API-KEY", "Authorization"]);
        services.AddScoped<IChatAiStore>(provider => new EfChatAiStore(
            provider.GetService<IDbContextFactory<ChatDbContext>>() ?? throw new ChatMessageDependencyUnavailableException("chat database"),
            options, provider.GetRequiredService<IChatMessageEventDelivery>(),
            () => provider.GetRequiredService<ChatReadLocalTime>().FromUtc(provider.GetRequiredService<TimeProvider>().GetUtcNow()),
            Random.Shared.Next, provider.GetRequiredService<ILogger<EfChatAiStore>>(),
            provider.GetService<IChatAiUserNameReader>(), provider.GetService<IChatMessageProfileReader>()));
        services.AddScoped(provider => new ChatAiProcessor(provider.GetRequiredService<IChatAiStore>(),
            provider.GetRequiredService<IChatAiReplyClient>(), provider.GetRequiredService<IChatAiDelay>(), Random.Shared.NextDouble,
            (stage, failure) => provider.GetRequiredService<ILogger<ChatAiProcessor>>()
                .LogWarning("Chat AI processing failed. Stage={Stage}, Failure={FailureType}", stage, failure)));
        return services;
    }
}
