using System.Security.Claims;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.Read;

namespace TranslaCat.Chat.Api.Runtime;

public sealed class AuthenticatedReadRecipient(IHttpContextAccessor httpContext) : IChatReadRecipientResolver
{
    public Task<string?> ResolveUsernameAsync(long userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var principal = httpContext.HttpContext?.User;

        // 현재 요청의 검증된 principal만 재사용한다. 타 사용자 email이나 BE DB를 여기서 조회하지 않는다.
        if (principal is null || !ChatReadAuthorization.TryGetUserId(principal, out var currentId)
            || currentId != userId || principal.Identity?.IsAuthenticated != true)
        {
            throw new ChatReadAdapterUnavailableException();
        }

        var email = principal.FindFirstValue(ClaimTypes.Name);
        return Task.FromResult(string.IsNullOrWhiteSpace(email) ? null : email);
    }
}
