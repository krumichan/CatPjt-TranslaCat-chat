using TranslaCat.Chat.Api.Read;

namespace TranslaCat.Chat.Api.ServiceAuthentication;

public sealed class ChatServiceAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ChatServiceIngressGuard guard)
    {
        var path = context.Request.Path;
        var realtime = path.StartsWithSegments("/ws/chat");
        var admin = path.StartsWithSegments("/api/v1/admin/chat");
        var business = realtime || admin || path.StartsWithSegments("/api/v1/chat")
            || path.StartsWithSegments("/api/v1/users/me/chat-language-settings");
        if (!business || !guard.IsRequired)
        {
            await next(context);
            return;
        }

        // health/readiness와 업무 ingress를 분리한다. 미구성도 서비스 인증 성공으로 취급하지 않는다.
        var scope = realtime ? "chat:realtime" : admin ? "chat:admin" : "chat:http";
        var identity = await guard.AuthenticateAsync(context, scope);
        if (identity is null)
        {
            await RejectAsync(context, "CHAT_SERVICE_AUTH_REQUIRED");
            return;
        }

        // HTTP 사용자는 앞선 실제 사용자 인증 결과와 대조한다. WS는 CONNECT에서 별도로 대조한다.
        if (!realtime && (!ChatReadAuthorization.TryGetUserId(context.User, out var userId)
            || userId <= 0 || userId != identity.UserId))
        {
            await RejectAsync(context, "CHAT_SERVICE_USER_MISMATCH");
            return;
        }

        if (admin && !context.User.IsInRole("ROLE_ADMIN"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "CHAT_SERVICE_ADMIN_REQUIRED"
            }, context.RequestAborted);
            return;
        }

        await next(context);
    }

    private static async Task RejectAsync(HttpContext context, string code)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new
        {
            code,
            message = "Chat ingress authentication is required."
        }, context.RequestAborted);
    }
}
