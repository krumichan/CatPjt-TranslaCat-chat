using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TranslaCat.Chat.Api.ServiceAuthentication;
using TranslaCat.Chat.Application.Core;

namespace TranslaCat.Chat.Api.Core;

public sealed class ChatCoreAccessTokenProvider(ChatServiceTokenSettings settings, string environment, TimeProvider clock)
    : IChatCoreAccessTokenProvider
{
    private static readonly HashSet<string> Scopes = ["chat:accounts:read", "chat:relations:read", "chat:storage:read", "chat:storage:write", "chat:storage:delete"];

    public string CreateToken(string scope)
    {
        if (!Scopes.Contains(scope) || !ChatServiceJwt.IsConfigured(settings))
        {
            throw new ChatCoreUnavailableException();
        }

        var now = clock.GetUtcNow();

        // worker도 사용자를 합성하지 않는다. subject=서비스와 단일 operation scope를 명시한다.
        var claims = new Dictionary<string, object>
        {
            ["iss"] = settings.Issuer!,
            ["aud"] = settings.Audience!,
            ["sub"] = settings.Service!,
            ["service"] = settings.Service!,
            ["tokenUse"] = "chat-core-service",
            ["environment"] = environment,
            ["scopes"] = new[] { scope },
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddSeconds(ChatServiceJwt.MaximumLifetimeSeconds).ToUnixTimeSeconds()
        };
        var key = new SymmetricSecurityKey(Convert.FromBase64String(settings.Base64SigningKey!));
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }
            .CreateToken(JsonSerializer.Serialize(claims), new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
    }
}
