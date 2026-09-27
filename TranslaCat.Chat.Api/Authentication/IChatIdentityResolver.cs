namespace TranslaCat.Chat.Api.Authentication;

public sealed record ChatIdentityLookup(string Subject, long TokenUserId);

public sealed record ChatResolvedIdentity(long UserId, string Email, string Role, bool CanAuthenticate);

public interface IChatIdentityResolver
{
    // 현재 계정 소유자가 확인한 상태를 반환한다. JWT claims나 Chat DB의 가짜 user row로 대체하지 않는다.
    // 계정 부재/탈퇴/차단은 null 또는 CanAuthenticate=false, 조회 장애는 실패로 반환해야 한다.
    Task<ChatResolvedIdentity?> ResolveAsync(ChatIdentityLookup lookup, CancellationToken cancellationToken);
}
