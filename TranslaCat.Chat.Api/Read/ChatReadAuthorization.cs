using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace TranslaCat.Chat.Api.Read;

public static class ChatReadAuthorization
{
    public const string Policy = "ChatRead";

    public static bool TryGetUserId(ClaimsPrincipal principal, out long userId)
    {
        // 인증 adapter가 검증한 identity만 신뢰한다. HTTP 헤더나 미검증 JWT를 직접 읽지 않는다.
        var identifiers = principal.Identities
            .Where(identity => identity.IsAuthenticated)
            .SelectMany(identity => identity.FindAll(ClaimTypes.NameIdentifier))
            .Select(claim => claim.Value)
            .ToArray();

        userId = default;

        return identifiers.Length == 1
            && long.TryParse(identifiers[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out userId);
    }
}

public sealed class ChatReadAuthorizationResultHandler(ChatReadHttpResponses responses)
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        // 읽음 endpoint만 이 경계를 사용한다. 인증 scheme 부재도 성공이나 500으로 바꾸지 않는다.
        if (context.GetEndpoint()?.Metadata.GetMetadata<ChatReadEndpointAttribute>() is null
            || authorizeResult.Succeeded)
        {
            await fallback.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        var status = authorizeResult.Challenged
            ? StatusCodes.Status401Unauthorized
            : StatusCodes.Status403Forbidden;
        var code = status == StatusCodes.Status401Unauthorized ? "UNAUTHORIZED" : "ACCESS_DENIED";

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(
            responses.Error(context, status, code, $"Message <{code}>"),
            context.RequestAborted);
    }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ChatReadEndpointAttribute : Attribute;
