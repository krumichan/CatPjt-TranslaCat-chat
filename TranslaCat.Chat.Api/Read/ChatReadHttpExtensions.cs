using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Api.ServiceAuthentication;
using TranslaCat.Chat.Application.Read;

namespace TranslaCat.Chat.Api.Read;

public static class ChatReadHttpExtensions
{
    public static IServiceCollection AddChatReadHttp(this IServiceCollection services)
    {
        // framework와 기존 Application만 연결한다. 운영 인증·저장소 adapter는 여기서 대체하지 않는다.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new ChatReadLocalTime(TimeZoneInfo.Local));
        services.TryAddSingleton(provider => new ChatReadContractMapper(
            provider.GetRequiredService<ChatReadLocalTime>().SourceTimeZone));
        services.AddSingleton<ChatReadHttpResponses>();
        services.AddScoped(provider => new ChatRoomReadService(
            provider.GetService<IChatReadTransaction>() ?? throw new ChatReadAdapterUnavailableException(),
            () => provider.GetRequiredService<ChatReadLocalTime>().FromUtcForReadPersistence(
                provider.GetRequiredService<TimeProvider>().GetUtcNow())));

        // 외부 검증을 마친 인증 identity의 내부 user ID가 필요하다. 기본 인증 scheme은 설치하지 않는다.
        services.AddAuthentication();
        services.AddAuthorization(options => options.AddPolicy(ChatReadAuthorization.Policy, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.RequireAssertion(context => ChatReadAuthorization.TryGetUserId(context.User, out _));
        }));
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ChatReadAuthorizationResultHandler>();

        services.AddControllers()
            .AddApplicationPart(typeof(ChatReadHttpExtensions).Assembly)
            .AddJsonOptions(options =>
            {
                // Jackson 필드 이름과 일치시킨다. 정밀한 long 입력 처리는 DTO converter가 담당한다.
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = false;
            });
        services.Configure<ApiBehaviorOptions>(options =>
        {
            var original = options.InvalidModelStateResponseFactory;
            options.InvalidModelStateResponseFactory = context =>
            {
                if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<ChatReadEndpointAttribute>() is null)
                {
                    return original(context);
                }

                // BE 전역 Advice의 catch-all 경로를 보존한다. 400 정규화는 별도 공개 계약 결정이다.
                var responses = context.HttpContext.RequestServices.GetRequiredService<ChatReadHttpResponses>();
                return new ObjectResult(responses.Error(
                    context.HttpContext,
                    500,
                    "",
                    "Message <읽음 요청 형식 또는 입력 값이 올바르지 않습니다.>"))
                {
                    StatusCode = 500
                };
            };
        });

        return services;
    }

    public static WebApplication UseChatReadHttp(this WebApplication app)
    {
        // 테스트도 이 파이프라인을 사용하여 routing·인증 경계·binding·오류 처리·직렬화를 통과한다.
        app.UseRouting();
        app.UseAuthentication();
        if (app.Services.GetService<ChatServiceIngressGuard>() is not null)
        {
            app.UseChatServiceAuthentication();
        }
        app.UseAuthorization();
        app.UseMiddleware<ChatReadExceptionMiddleware>();
        app.MapControllers();

        return app;
    }
}
