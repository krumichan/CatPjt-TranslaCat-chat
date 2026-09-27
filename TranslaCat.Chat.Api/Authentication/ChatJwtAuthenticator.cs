using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TranslaCat.Chat.Api.Authentication;

public sealed class ChatJwtAuthenticator(
    IOptions<ChatJwtOptions> options,
    TimeProvider timeProvider,
    IChatIdentityResolver? identityResolver)
{
    public const string AuthenticationScheme = "ChatBearer";

    public async Task<ClaimsPrincipal?> AuthenticateAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 설정과 현재 계정 조회가 모두 준비되어야 인증할 수 있다. 무서명 claims로 보완하지 않는다.
        var key = ReadSigningKey();
        if (key is null || identityResolver is null || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var parameters = new TokenValidationParameters
        {
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256, SecurityAlgorithms.HmacSha384, SecurityAlgorithms.HmacSha512],
            // 현재 BE 발급 토큰에는 issuer/audience가 없다. 임의 발급 체계를 추가하지 않는다.
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero,
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;
                return expires.HasValue
                    && expires.Value >= now
                    && (!notBefore.HasValue || (notBefore.Value <= now && notBefore.Value <= expires.Value));
            }
        };

        // Microsoft의 서명/알고리즘 검증을 실제 실행한다. 실패 exception/token을 로그나 응답에 노출하지 않는다.
        var validation = await handler.ValidateTokenAsync(token, parameters);
        cancellationToken.ThrowIfCancellationRequested();
        if (!validation.IsValid || validation.ClaimsIdentity is null)
        {
            return null;
        }

        var subjects = validation.ClaimsIdentity.FindAll("sub").ToArray();
        var userIds = validation.ClaimsIdentity.FindAll("id").ToArray();
        if (subjects.Length != 1 || string.IsNullOrWhiteSpace(subjects[0].Value)
            || userIds.Length != 1
            || !long.TryParse(userIds[0].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var tokenUserId))
        {
            return null;
        }

        // BE도 subject로 현재 user를 조회한다. 조회된 ID/권한을 사용하고 서명된 id와 충돌하면 거부한다.
        ChatResolvedIdentity? resolved;
        try
        {
            resolved = await identityResolver.ResolveAsync(new ChatIdentityLookup(subjects[0].Value, tokenUserId), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (resolved is null || !resolved.CanAuthenticate
            || resolved.UserId != tokenUserId
            || !string.Equals(resolved.Email, subjects[0].Value, StringComparison.Ordinal)
            || resolved.Role is not ("ROLE_USER" or "ROLE_ADMIN"))
        {
            return null;
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, resolved.UserId.ToString(CultureInfo.InvariantCulture)),
             new Claim(ClaimTypes.Name, resolved.Email),
             new Claim(ClaimTypes.Role, resolved.Role)],
            AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    private SymmetricSecurityKey? ReadSigningKey()
    {
        if (!options.Value.Enabled || string.IsNullOrWhiteSpace(options.Value.Base64SigningKey))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(options.Value.Base64SigningKey);
            return bytes.Length >= 32 ? new SymmetricSecurityKey(bytes) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
