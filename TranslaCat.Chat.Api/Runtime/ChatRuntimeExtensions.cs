using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using TranslaCat.Chat.Api.Ai;
using TranslaCat.Chat.Api.AiManagement;
using TranslaCat.Chat.Api.Authentication;
using TranslaCat.Chat.Api.Configuration;
using TranslaCat.Chat.Api.Language;
using TranslaCat.Chat.Api.Membership;
using TranslaCat.Chat.Api.MembershipQuery;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Notifications;
using TranslaCat.Chat.Api.OpenMembership;
using TranslaCat.Chat.Api.OpenModeration;
using TranslaCat.Chat.Api.OpenRooms;
using TranslaCat.Chat.Api.Presence;
using TranslaCat.Chat.Api.ProfileImages;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Realtime;
using TranslaCat.Chat.Api.Rooms;
using TranslaCat.Chat.Api.Translation;
using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Presence;
using TranslaCat.Chat.Application.Read;
using TranslaCat.Chat.Application.Realtime;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Application.Translation;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.Repositories;
using TranslaCat.Chat.Infrastructure.Redis;

namespace TranslaCat.Chat.Api.Runtime;

public static class ChatRuntimeExtensions
{
    public static IServiceCollection AddChatRuntime(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddChatJwtAuthentication(configuration);
        services.AddChatRealtime();
        var realtime = configuration.GetSection("Chat:Realtime").Get<ChatRealtimeOptions>() ?? new();
        realtime.Validate();
        services.Replace(ServiceDescriptor.Singleton(realtime));
        services.AddChatMessageHttp();
        services.AddChatLanguageHttp();
        services.AddChatNotificationHttp();
        services.AddChatMembershipHttp();
        services.AddOpenRoomHttp();
        services.AddOpenMembershipHttp();
        services.AddOpenModerationHttp();
        services.AddChatAiManagementHttp();
        services.AddChatMemberQueryHttp();
        services.AddChatProfileImageHttp();
        services.AddChatTranslation(configuration.GetSection("Chat:Translation").Get<ChatTranslationOptions>() ?? new());
        services.AddChatAi(configuration.GetSection("Chat:Ai").Get<ChatAiOptions>() ?? new());
        services.AddHttpContextAccessor();
        services.AddSingleton<ChatRoomContractMapper>();
        services.AddScoped(provider => new ChatRoomService(
            provider.GetService<IChatRoomStore>() ?? throw new ChatRoomDependencyUnavailableException(),
            () => provider.GetRequiredService<ChatReadLocalTime>().FromUtc(provider.GetRequiredService<TimeProvider>().GetUtcNow())));
        var zoneId = configuration["Chat:SourceTimeZone"];
        if (!string.IsNullOrWhiteSpace(zoneId))
        {
            services.Replace(ServiceDescriptor.Singleton(new ChatReadLocalTime(TimeZoneInfo.FindSystemTimeZoneById(zoneId))));
        }

        // 설정이 있을 때만 실제 DB adapter를 연결한다. API startup에서는 migration/seed를 실행하지 않는다.
        var database = configuration["Chat:Database:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(database))
        {
            services.AddChatDatabase(database);
        }

        var redis = ChatRedisConfiguration.GetConnectionString(configuration);
        if (!string.IsNullOrWhiteSpace(redis))
        {
            var prefix = configuration["Chat:Redis:Namespace"] ?? throw new InvalidOperationException("Chat Redis namespace is required.");
            var presence = configuration.GetSection("Chat:Presence").Get<ChatPresenceOptions>() ?? new();
            services.AddChatRedis(redis, prefix, presence);
        }

        return services;
    }

    public static IServiceCollection AddChatDatabase(this IServiceCollection services, string connectionString, bool isolatedTestCatalog = false)
    {
        var validated = ChatDatabaseTarget.Validate(connectionString, isolatedTestCatalog);
        services.AddDbContextFactory<ChatDbContext>(options => options.UseMySQL(validated));
        services.AddOpenRoomPersistence();
        services.AddOpenMembershipPersistence();
        services.AddOpenModerationPersistence();
        services.TryAddScoped<IChatReadRecipientResolver, AuthenticatedReadRecipient>();
        services.AddScoped<IChatReadTransaction>(provider => new EfChatReadTransaction(
            provider.GetRequiredService<IDbContextFactory<ChatDbContext>>(),
            provider.GetRequiredService<IChatReadRecipientResolver>(),
            provider.GetService<IChatReadEventDelivery>() ?? throw new ChatReadAdapterUnavailableException(),
            provider.GetRequiredService<ILogger<EfChatReadTransaction>>()));
        services.AddScoped<IChatRealtimeAccess, EfChatRealtimeAccess>();
        services.AddScoped<IChatMessageEventDelivery, ChatMessageRealtimeDelivery>();
        services.AddScoped<IChatMessageTransaction>(provider => new EfChatMessageTransaction(
            provider.GetRequiredService<IDbContextFactory<ChatDbContext>>(),
            provider.GetService<IChatMessageProfileReader>() ?? throw new ChatMessageDependencyUnavailableException("account profile/storage"),
            provider.GetRequiredService<IChatMessageEventDelivery>(),
            provider.GetRequiredService<ILogger<EfChatMessageTransaction>>()));
        services.AddScoped<IChatRealtimeMessageSender>(provider => provider.GetRequiredService<ChatMessageService>());
        services.AddScoped<IChatRoomStore>(provider => new EfChatRoomStore(
            provider.GetRequiredService<IDbContextFactory<ChatDbContext>>(), provider.GetService<IChatRoomAccountReader>(),
            provider.GetService<ChatPresenceCoordinator>()));
        return services;
    }

    public static IServiceCollection AddChatRedis(this IServiceCollection services, string connectionString, string prefix, ChatPresenceOptions presence)
    {
        RedisChatNamespace.Validate(prefix);
        presence.Validate();
        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(connectionString);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });
        services.AddSingleton(provider => new RedisChatEventBus(provider.GetRequiredService<IConnectionMultiplexer>(), prefix));
        services.AddSingleton<ChatRealtimeRedisRelay>();
        services.AddHostedService(provider => provider.GetRequiredService<ChatRealtimeRedisRelay>());
        services.AddSingleton<IChatReadEventDelivery, ChatReadRealtimeDelivery>();

        // Presence는 명시적으로 켠 환경에서만 worker가 실행된다. 프로세스당 multiplexer 하나를 공유한다.
        services.AddSingleton(presence);
        services.AddSingleton<IChatPresenceStore>(provider => new RedisChatPresenceStore(
            provider.GetRequiredService<IConnectionMultiplexer>(), presence, prefix, provider.GetRequiredService<TimeProvider>()));
        services.AddChatPresenceFanout();
        services.AddSingleton(provider => new RedisChatPresencePublisher(
            provider.GetRequiredService<RedisChatEventBus>(),
            async change =>
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ChatPresenceFanout>().HandleAsync(change);
            },
            (operation, exception) => provider.GetRequiredService<ILogger<RedisChatPresencePublisher>>()
                .LogWarning("Chat presence {Operation} failed ({FailureType}).", operation, exception.GetType().Name)));
        services.AddSingleton<IChatPresencePublisher>(provider => provider.GetRequiredService<RedisChatPresencePublisher>());
        services.AddSingleton(provider => new ChatPresenceCoordinator(
            provider.GetRequiredService<IChatPresenceStore>(), provider.GetRequiredService<IChatPresencePublisher>(),
            presence, provider.GetRequiredService<TimeProvider>(),
            () => provider.GetRequiredService<ChatReadLocalTime>().FromUtc(provider.GetRequiredService<TimeProvider>().GetUtcNow()),
            (operation, exception) => provider.GetRequiredService<ILogger<ChatPresenceCoordinator>>()
                .LogWarning("Chat presence {Operation} failed ({FailureType}).", operation, exception.GetType().Name)));
        services.AddChatPresenceRuntime();
        return services;
    }
}
