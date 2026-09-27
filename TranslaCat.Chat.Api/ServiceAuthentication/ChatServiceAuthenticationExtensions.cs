using Microsoft.Extensions.DependencyInjection.Extensions;
using TranslaCat.Chat.Api.Authentication;

namespace TranslaCat.Chat.Api.ServiceAuthentication;

public static class ChatServiceAuthenticationExtensions
{
    public static IServiceCollection AddChatServiceAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ChatServiceIngressOptions>(configuration.GetSection(ChatServiceIngressOptions.SectionName));
        services.Configure<ChatIdentityOptions>(configuration.GetSection(ChatIdentityOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ChatServiceIngressGuard>();

        // 고정 upstream만 사용하고 redirect/cookie 전달을 끈다. 토큰은 HTTP client 진단에서도 가린다.
        services.AddHttpClient<HttpChatIdentityResolver>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            .RedactLoggedHeaders(["Authorization", ChatServiceIngressGuard.HeaderName]);
        // 비활성 client를 identity adapter로 등록하면 readiness가 준비 상태를 잘못 표시할 수 있다.
        if (configuration.GetValue<bool>(ChatIdentityOptions.SectionName + ":Enabled"))
        {
            services.TryAddScoped<IChatIdentityResolver>(provider => provider.GetRequiredService<HttpChatIdentityResolver>());
        }
        return services;
    }

    public static IApplicationBuilder UseChatServiceAuthentication(this IApplicationBuilder application)
    {
        return application.UseMiddleware<ChatServiceAuthenticationMiddleware>();
    }
}
