using System.Security.Claims;
using Microsoft.Extensions.Options;
using TranslaCat.Chat.Api.Read;

namespace TranslaCat.Chat.Api.ServiceAuthentication;

public sealed class ChatServiceIngressGuard(IOptions<ChatServiceIngressOptions> options,
    IHostEnvironment environment, TimeProvider timeProvider)
{
    public const string HeaderName = "X-Chat-Service-Authorization";
    private static readonly object ContextKey = new();
    private static readonly HashSet<string> Scopes = ["chat:http", "chat:realtime", "chat:admin"];

    public bool IsRequired => !environment.IsEnvironment("Testing") || options.Value.Enabled;

    public async Task<ChatVerifiedServiceIdentity?> AuthenticateAsync(HttpContext context, string requiredScope)
    {
        // 브라우저가 보낸 임의 사용자 헤더는 읽지 않는다. 이 헤더는 BE 서버가 주입하는 별도 서명 토큰이다.
        var values = context.Request.Headers[HeaderName];
        if (!options.Value.Enabled || values.Count != 1 || values[0] is not { } header
            || !header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return null;
        }

        var verified = await ChatServiceJwt.ValidateAsync(header[7..], options.Value,
            environment.EnvironmentName, "chat-ingress", Scopes, timeProvider.GetUtcNow(), context.RequestAborted);
        if (verified is null || !verified.Scopes.Contains(requiredScope, StringComparer.Ordinal))
        {
            return null;
        }

        context.Items[ContextKey] = verified;
        return verified;
    }

    public bool ValidateRealtimeUser(HttpContext context, ClaimsPrincipal principal)
    {
        if (!IsRequired)
        {
            return true;
        }

        // handshake의 서비스 인증만으로 STOMP 사용자를 정하지 않는다. 실제 CONNECT 인증 결과와 일치해야 한다.
        return context.Items.TryGetValue(ContextKey, out var value) && value is ChatVerifiedServiceIdentity verified
            && verified.Scopes.Contains("chat:realtime", StringComparer.Ordinal)
            && ChatReadAuthorization.TryGetUserId(principal, out var userId) && userId > 0 && userId == verified.UserId;
    }
}
