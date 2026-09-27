using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TranslaCat.Chat.Api.ServiceAuthentication;

namespace TranslaCat.Chat.ApiTests.ServiceAuthentication;

public sealed class ChatServiceJwtTests
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    internal static readonly HashSet<string> AllowedScopes = ["chat:http", "chat:realtime", "chat:admin"];

    [Theory]
    [InlineData(32, 73L)]
    [InlineData(128, long.MaxValue)]
    public async Task Signed_scoped_service_identity_preserves_positive_int64(int keyLength, long userId)
    {
        // 준비
        var key = RandomNumberGenerator.GetBytes(keyLength);
        var settings = Settings(key);
        var token = ChatServiceJwt.Issue(settings, "Development", "chat-ingress", "chat:http", userId, Now);

        // 실행
        var identity = await Validate(token, settings);

        // 검증
        Assert.NotNull(identity);
        Assert.Equal(userId, identity.UserId);
        Assert.Equal(["chat:http"], identity.Scopes);
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("wrong-algorithm")]
    [InlineData("unsigned")]
    [InlineData("malformed")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("service")]
    [InlineData("token-use")]
    [InlineData("environment")]
    [InlineData("expired")]
    [InlineData("future-issued")]
    [InlineData("overlong-life")]
    [InlineData("reversed-life")]
    [InlineData("missing-issued")]
    [InlineData("missing-expiry")]
    [InlineData("text-issued")]
    [InlineData("text-expiry")]
    [InlineData("negative-subject")]
    [InlineData("zero-subject")]
    [InlineData("leading-zero-subject")]
    [InlineData("overflow-subject")]
    [InlineData("numeric-subject")]
    [InlineData("missing-scopes")]
    [InlineData("empty-scopes")]
    [InlineData("unknown-scope")]
    [InlineData("duplicate-scope")]
    [InlineData("scalar-scope")]
    [InlineData("duplicate-claim")]
    public async Task Wrong_credentials_context_types_and_lifetime_are_rejected(string failure)
    {
        // 준비: 실제 서명 토큰의 한 계약만 어긋나게 만든다.
        var key = RandomNumberGenerator.GetBytes(48);
        var claims = Claims();
        switch (failure)
        {
            case "issuer":
                claims["iss"] = "other";
                break;
            case "audience":
                claims["aud"] = "translacat-ll";
                break;
            case "service":
                claims["service"] = "translacat-ll";
                break;
            case "token-use":
                claims["tokenUse"] = "chat-identity";
                break;
            case "environment":
                claims["environment"] = "Production";
                break;
            case "expired":
                claims["iat"] = Now.AddSeconds(-120).ToUnixTimeSeconds();
                claims["exp"] = Now.AddSeconds(-5).ToUnixTimeSeconds();
                break;
            case "future-issued":
                claims["iat"] = Now.AddSeconds(6).ToUnixTimeSeconds();
                break;
            case "overlong-life":
                claims["exp"] = Now.AddSeconds(121).ToUnixTimeSeconds();
                break;
            case "reversed-life":
                claims["exp"] = Now.ToUnixTimeSeconds();
                break;
            case "missing-issued":
                claims.Remove("iat");
                break;
            case "missing-expiry":
                claims.Remove("exp");
                break;
            case "text-issued":
                claims["iat"] = Now.ToUnixTimeSeconds().ToString();
                break;
            case "text-expiry":
                claims["exp"] = Now.AddSeconds(120).ToUnixTimeSeconds().ToString();
                break;
            case "negative-subject":
                claims["sub"] = "-73";
                break;
            case "zero-subject":
                claims["sub"] = "0";
                break;
            case "leading-zero-subject":
                claims["sub"] = "073";
                break;
            case "overflow-subject":
                claims["sub"] = "9223372036854775808";
                break;
            case "numeric-subject":
                claims["sub"] = 73;
                break;
            case "missing-scopes":
                claims.Remove("scopes");
                break;
            case "empty-scopes":
                claims["scopes"] = Array.Empty<string>();
                break;
            case "unknown-scope":
                claims["scopes"] = new[] { "settings:read" };
                break;
            case "duplicate-scope":
                claims["scopes"] = new[] { "chat:http", "chat:http" };
                break;
            case "scalar-scope":
                claims["scopes"] = "chat:http";
                break;
        }
        var json = JsonSerializer.Serialize(claims);
        if (failure == "duplicate-claim")
        {
            json = json[..^1] + ",\"sub\":\"73\"}";
        }

        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        var token = failure switch
        {
            "malformed" => "invalid.token",
            "unsigned" => handler.CreateToken(json),
            _ => handler.CreateToken(json, new SigningCredentials(
                new SymmetricSecurityKey(failure == "wrong-key" ? RandomNumberGenerator.GetBytes(48) : key),
                failure == "wrong-algorithm" ? SecurityAlgorithms.HmacSha384 : SecurityAlgorithms.HmacSha256))
        };

        // 실행
        var identity = await Validate(token, Settings(key));

        // 검증
        Assert.Null(identity);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("invalid-base64")]
    [InlineData("short-key")]
    [InlineData("long-key")]
    [InlineData("missing-issuer")]
    public async Task Unconfigured_credentials_cannot_authenticate(string failure)
    {
        // 준비
        var key = RandomNumberGenerator.GetBytes(32);
        var settings = Settings(key);
        settings.Base64SigningKey = failure switch
        {
            "empty" => "",
            "invalid-base64" => "?",
            "short-key" => Convert.ToBase64String(new byte[31]),
            "long-key" => Convert.ToBase64String(new byte[129]),
            _ => settings.Base64SigningKey
        };
        if (failure == "missing-issuer")
        {
            settings.Issuer = "";
        }

        var token = Sign(key, Claims());

        // 실행 / 검증
        Assert.False(ChatServiceJwt.IsConfigured(settings));
        Assert.Null(await Validate(token, settings));
    }

    [Fact]
    public async Task Cancellation_remains_cancellation()
    {
        // 준비
        var key = RandomNumberGenerator.GetBytes(32);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // 실행 / 검증
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChatServiceJwt.ValidateAsync(
            Sign(key, Claims()), Settings(key), "Development", "chat-ingress", AllowedScopes, Now, cancellation.Token));
    }

    internal static ChatServiceTokenSettings Settings(byte[] key)
    {
        return new()
        {
            Issuer = "translacat-be",
            Audience = "translacat-chat",
            Service = "translacat-be",
            Base64SigningKey = Convert.ToBase64String(key)
        };
    }

    internal static Dictionary<string, object> Claims(string environment = "Development", string scope = "chat:http", string subject = "73")
    {
        return new()
        {
            ["iss"] = "translacat-be",
            ["aud"] = "translacat-chat",
            ["sub"] = subject,
            ["service"] = "translacat-be",
            ["tokenUse"] = "chat-ingress",
            ["environment"] = environment,
            ["scopes"] = new[] { scope },
            ["iat"] = Now.ToUnixTimeSeconds(),
            ["exp"] = Now.AddSeconds(120).ToUnixTimeSeconds()
        };
    }

    internal static string Sign(byte[] key, Dictionary<string, object> claims)
    {
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(JsonSerializer.Serialize(claims),
                new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256));
    }

    private static Task<ChatVerifiedServiceIdentity?> Validate(string token, ChatServiceTokenSettings settings)
    {
        return ChatServiceJwt.ValidateAsync(token, settings, "Development", "chat-ingress", AllowedScopes, Now);
    }
}
