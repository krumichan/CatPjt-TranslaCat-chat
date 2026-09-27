using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.ServiceAuthentication;
using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.MembershipQuery;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.Presence;
using TranslaCat.Chat.Application.ProfileImages;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Core;

namespace TranslaCat.Chat.Api.Core;

public sealed class ChatCoreOptions
{
    public bool Enabled
    {
        get; set;
    }
    public string? BaseUrl
    {
        get; set;
    }
    public int TimeoutSeconds { get; set; } = 5;
    public ChatServiceTokenSettings ServiceAuthentication { get; set; } = new();
}

public static class ChatCoreExtensions
{
    public static IServiceCollection AddChatCore(this IServiceCollection services, IConfiguration configuration, string environmentName)
    {
        ChatCoreOptions options;
        Uri? origin;
        try
        {
            options = configuration.GetSection("Chat:Core").Get<ChatCoreOptions>() ?? new();

            // 비활성 adapter의 존재만으로 readiness가 준비 상태로 오인하지 않도록 alias도 등록하지 않는다.
            if (!options.Enabled)
            {
                return services;
            }

            // 평문은 Development loopback에만 허용하며 base path나 credential이 섞인 URL은 받지 않는다.
            if (options.TimeoutSeconds is < 1 or > 30
                || !ChatServiceJwt.IsConfigured(options.ServiceAuthentication)
                || !Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out origin)
                || origin.AbsolutePath != "/"
                || origin.UserInfo.Length != 0
                || origin.Query.Length != 0
                || origin.Fragment.Length != 0
                || (origin.Scheme != "https"
                    && !(environmentName == Environments.Development && origin.Scheme == "http" && origin.IsLoopback)))
            {
                throw new InvalidOperationException();
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException)
        {
            throw new InvalidOperationException("Chat Core origin, service credential or timeout is invalid.");
        }

        // 실제 HTTP adapter만 연결한다. 계정·관계·storage의 fake 또는 로컬 DB 복제 fallback은 없다.
        services.AddSingleton(new ChatCoreHttpSettings(origin, TimeSpan.FromSeconds(options.TimeoutSeconds)));
        services.AddSingleton<IChatCoreAccessTokenProvider>(provider => new ChatCoreAccessTokenProvider(
            options.ServiceAuthentication, environmentName, provider.GetRequiredService<TimeProvider>()));
        services.AddHttpClient<HttpChatCoreClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RedactLoggedHeaders(["Authorization", "X-Chat-Object-Key"])
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
            });
        services.AddScoped<HttpChatCoreDirectory>();
        services.AddScoped<HttpChatCoreStorage>();
        services.AddScoped<IChatMessageProfileReader>(provider => provider.GetRequiredService<HttpChatCoreDirectory>());
        services.AddScoped<IChatRoomAccountReader>(provider => provider.GetRequiredService<HttpChatCoreDirectory>());
        services.AddScoped<IChatMembershipDirectory>(provider => provider.GetRequiredService<HttpChatCoreDirectory>());
        services.AddScoped<IChatMemberProfileReader>(provider => provider.GetRequiredService<HttpChatCoreDirectory>());
        services.AddScoped<IChatNotificationProfileReader>(provider => provider.GetRequiredService<HttpChatCoreDirectory>());
        services.AddScoped<IChatPresenceProfileReader>(provider => provider.GetRequiredService<HttpChatCoreDirectory>());
        services.AddScoped<IChatAiUserNameReader>(provider => provider.GetRequiredService<HttpChatCoreDirectory>());
        services.AddScoped<IOpenProfileStorage>(provider => provider.GetRequiredService<HttpChatCoreStorage>());
        services.AddScoped<IChatAiProfileStorage>(provider => provider.GetRequiredService<HttpChatCoreStorage>());
        services.AddScoped<IChatProfileImageObjectStore>(provider => provider.GetRequiredService<HttpChatCoreStorage>());
        services.Configure<MvcOptions>(mvc => mvc.Filters.Add<ChatCoreUnavailableFilter>(int.MaxValue));

        return services;
    }
}

public sealed class ChatCoreUnavailableFilter(ChatReadHttpResponses responses) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not ChatCoreUnavailableException)
        {
            return;
        }

        // 각 기능의 기존 500 fallback보다 먼저 처리하되 다른 업무 오류에는 관여하지 않는다.
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        context.Result = new ObjectResult(responses.Error(
            context.HttpContext,
            StatusCodes.Status503ServiceUnavailable,
            "CHAT_CORE_UNAVAILABLE",
            "Chat Core dependency is unavailable."))
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable
        };
        context.ExceptionHandled = true;
    }
}
