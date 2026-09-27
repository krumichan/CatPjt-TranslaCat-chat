using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace TranslaCat.Chat.Api.Authentication;

public static class ChatJwtAuthenticationExtensions
{
    public static IServiceCollection AddChatJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // 기본 설정은 비활성이다. 실제 shared identity adapter가 없으면 서명이 맞아도 인증하지 않는다.
        services.Configure<ChatJwtOptions>(configuration.GetSection(ChatJwtOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped(provider => new ChatJwtAuthenticator(
            provider.GetRequiredService<IOptions<ChatJwtOptions>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<IChatIdentityResolver>()));

        services.AddAuthentication(ChatJwtAuthenticator.AuthenticationScheme)
            .AddScheme<AuthenticationSchemeOptions, ChatJwtAuthenticationHandler>(ChatJwtAuthenticator.AuthenticationScheme, _ => { });

        return services;
    }
}
