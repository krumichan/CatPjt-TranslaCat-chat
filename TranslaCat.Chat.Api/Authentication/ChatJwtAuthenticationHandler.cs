using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TranslaCat.Chat.Api.Authentication;

public sealed class ChatJwtAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ChatJwtAuthenticator authenticator) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // HTTP의 토큰 위치는 Authorization뿐이다. query/body/userId 헤더는 인증 입력으로 사용하지 않는다.
        if (!Request.Headers.TryGetValue("Authorization", out var values))
        {
            return AuthenticateResult.NoResult();
        }

        if (values.Count != 1 || values[0] is not string authorization
            || !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("CHAT_AUTHENTICATION_FAILED");
        }

        var principal = await authenticator.AuthenticateAsync(authorization[7..], Context.RequestAborted);
        return principal is null
            ? AuthenticateResult.Fail("CHAT_AUTHENTICATION_FAILED")
            : AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
