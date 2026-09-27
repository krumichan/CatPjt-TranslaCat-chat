using System.Globalization;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TranslaCat.Chat.Api.ServiceAuthentication;

public static class ChatServiceJwt
{
    public const int MaximumLifetimeSeconds = 120;
    public const int ClockSkewSeconds = 5;
    public const int MaximumTokenLength = 8192;

    public static bool IsConfigured(ChatServiceTokenSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.Issuer)
               && !string.IsNullOrWhiteSpace(settings.Audience)
               && !string.IsNullOrWhiteSpace(settings.Service)
               && ReadKey(settings) is not null;
    }

    public static string Issue(ChatServiceTokenSettings settings, string environment, string tokenUse,
        string scope, long userId, DateTimeOffset now)
    {
        // 기존 사용자 JWT와 다른 방향별 서비스 키로 짧은 토큰만 발급한다.
        var key = ReadKey(settings);
        if (!IsConfigured(settings) || key is null || userId <= 0 || string.IsNullOrWhiteSpace(environment))
        {
            throw new InvalidOperationException("Chat service authentication is not configured.");
        }

        var claims = new Dictionary<string, object>
        {
            ["iss"] = settings.Issuer!,
            ["aud"] = settings.Audience!,
            ["sub"] = userId.ToString(CultureInfo.InvariantCulture),
            ["service"] = settings.Service!,
            ["tokenUse"] = tokenUse,
            ["environment"] = environment,
            ["scopes"] = new[] { scope },
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddSeconds(MaximumLifetimeSeconds).ToUnixTimeSeconds()
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }
            .CreateToken(JsonSerializer.Serialize(claims), new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
    }

    public static async Task<ChatVerifiedServiceIdentity?> ValidateAsync(string token,
        ChatServiceTokenSettings settings, string environment, string tokenUse,
        IReadOnlySet<string> allowedScopes, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ReadKey(settings);
        if (!IsConfigured(settings) || key is null || string.IsNullOrEmpty(token) || token.Length > MaximumTokenLength)
        {
            return null;
        }

        // 서명, 알고리즘, 발급자, 수신자 검증이 먼저다. 내부 JWT도 미검증 claims를 신원으로 사용하지 않는다.
        var handler = new JsonWebTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = MaximumTokenLength };
        var parameters = new TokenValidationParameters
        {
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.Audience,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(ClockSkewSeconds),
            LifetimeValidator = (notBefore, expires, _, _) => expires.HasValue
                && expires.Value > now.AddSeconds(-ClockSkewSeconds).UtcDateTime
                && (!notBefore.HasValue || notBefore.Value <= now.AddSeconds(ClockSkewSeconds).UtcDateTime)
        };
        var validation = await handler.ValidateTokenAsync(token, parameters);
        cancellationToken.ThrowIfCancellationRequested();
        if (!validation.IsValid)
        {
            return null;
        }

        // 타입, 중복 필드, 사용자 범위와 최대 수명을 추가로 제한한다. 다른 서비스/환경/용도의 토큰은 거부한다.
        try
        {
            using var json = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(token.Split('.')[1]));
            var payload = json.RootElement;
            if (payload.ValueKind != JsonValueKind.Object
                || payload.EnumerateObject().Select(value => value.Name).Distinct(StringComparer.Ordinal).Count()
                   != payload.EnumerateObject().Count()
                || !Matches(payload, "service", settings.Service!) || !Matches(payload, "tokenUse", tokenUse)
                || !Matches(payload, "environment", environment)
                || !payload.TryGetProperty("sub", out var subject) || subject.ValueKind != JsonValueKind.String
                || !TryPositiveId(subject.GetString(), out var userId)
                || !payload.TryGetProperty("iat", out var issued) || !issued.TryGetInt64(out var issuedSeconds)
                || !payload.TryGetProperty("exp", out var expires) || !expires.TryGetInt64(out var expiresSeconds)
                || expiresSeconds <= issuedSeconds || expiresSeconds - (decimal)issuedSeconds > MaximumLifetimeSeconds
                || issuedSeconds > now.ToUnixTimeSeconds() + ClockSkewSeconds
                || expiresSeconds <= now.ToUnixTimeSeconds() - ClockSkewSeconds
                || !payload.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var values = scopes.EnumerateArray().ToArray();
            if (values.Length == 0 || values.Length > allowedScopes.Count
                || values.Any(value => value.ValueKind != JsonValueKind.String))
            {
                return null;
            }
            var names = values.Select(value => value.GetString()!).ToArray();
            return names.Distinct(StringComparer.Ordinal).Count() == names.Length && names.All(allowedScopes.Contains)
                ? new ChatVerifiedServiceIdentity(userId, names) : null;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static bool Matches(JsonElement payload, string name, string expected)
    {
        return payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
               && string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    }

    private static bool TryPositiveId(string? value, out long userId)
    {
        userId = 0;
        return value is { Length: > 0 and <= 19 } && value[0] is >= '1' and <= '9'
            && value.All(character => character is >= '0' and <= '9')
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out userId) && userId > 0;
    }

    private static SymmetricSecurityKey? ReadKey(ChatServiceTokenSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Base64SigningKey))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(settings.Base64SigningKey);
            return bytes.Length is >= 32 and <= 128 ? new SymmetricSecurityKey(bytes) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
