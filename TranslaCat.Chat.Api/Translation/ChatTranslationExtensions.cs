using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Runtime;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Translation;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Translation;

namespace TranslaCat.Chat.Api.Translation;

public static class ChatTranslationExtensions
{
    public static IServiceCollection AddChatTranslation(this IServiceCollection services, ChatTranslationOptions options)
    {
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton(provider => new ChatTranslationContractMapper(provider.GetRequiredService<ChatReadLocalTime>().SourceTimeZone));
        services.AddSingleton(provider => new ChatTranslationQueue(options, () =>
        {
            var registered = provider.GetRequiredService<IServiceProviderIsService>();
            return registered.IsService(typeof(IDbContextFactory<ChatDbContext>))
                && registered.IsService(typeof(ChatRealtimeRedisRelay));
        }));
        services.AddSingleton<IChatTranslationDispatcher>(provider => provider.GetRequiredService<ChatTranslationQueue>());
        services.AddHostedService<ChatTranslationWorker>();
        services.AddSingleton<ChatExternalApiCircuitBreaker>();

        // 실제 AI HTTP adapter만 등록한다. 미구성 시 IsConfigured=false이며 provider 호출은 발생하지 않는다.
        services.AddHttpClient<IChatTranslationClient, HttpChatTranslationClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectTimeout = options.ConnectTimeout,
                AllowAutoRedirect = false
            })
            .RedactLoggedHeaders(["X-API-KEY", "Authorization"]);
        services.AddScoped<IChatTranslationStore>(provider => new EfChatTranslationStore(
            provider.GetService<IDbContextFactory<ChatDbContext>>() ?? throw new ChatMessageDependencyUnavailableException("chat database"),
            () => LocalNow(provider), provider.GetService<IChatMessageProfileReader>()));
        services.AddScoped<IChatTranslationEventDelivery>(provider => new ChatTranslationRealtimeDelivery(
            provider.GetService<ChatRealtimeRedisRelay>() ?? throw new ChatMessageDependencyUnavailableException("realtime relay"),
            provider.GetRequiredService<ChatTranslationContractMapper>(), provider.GetRequiredService<ChatReadLocalTime>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddScoped(provider => new ChatTranslationProcessor(
            provider.GetRequiredService<IChatTranslationStore>(), provider.GetRequiredService<IChatTranslationClient>(),
            provider.GetRequiredService<IChatTranslationEventDelivery>(), options, () => LocalNow(provider),
            (status, failure) => provider.GetRequiredService<ILogger<ChatTranslationProcessor>>()
                .LogWarning("Translation post-commit event failed. Status={Status}, Failure={FailureType}", status, failure)));
        services.AddScoped(provider => new ChatTranslationRetryService(
            provider.GetRequiredService<IChatTranslationStore>(), provider.GetRequiredService<IChatTranslationDispatcher>(),
            () => LocalNow(provider)));
        return services;
    }

    private static DateTime LocalNow(IServiceProvider provider)
    {
        return provider.GetRequiredService<ChatReadLocalTime>().FromUtc(provider.GetRequiredService<TimeProvider>().GetUtcNow());
    }
}
