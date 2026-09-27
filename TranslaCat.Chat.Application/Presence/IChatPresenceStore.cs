namespace TranslaCat.Chat.Application.Presence;

// 각 연산의 session/index/transition 판정은 공유 저장소에서 원자적으로 수행한다.
public interface IChatPresenceStore
{
    Task<long> RegisterSessionAsync(long userId, string sessionId, CancellationToken cancellationToken = default);
    Task<bool> RefreshSessionAsync(long userId, string sessionId, CancellationToken cancellationToken = default);
    Task<long> RemoveSessionAsync(long userId, string sessionId, CancellationToken cancellationToken = default);
    Task<long> GetActiveSessionCountAsync(long userId, CancellationToken cancellationToken = default);
    Task<bool> ClaimOnlineTransitionAsync(long userId, CancellationToken cancellationToken = default);
    Task<bool> ClaimOfflineTransitionAsync(long userId, CancellationToken cancellationToken = default);
}

public sealed record ChatPresenceChanged(long UserId, bool Online, DateTime OccurredAt);

public interface IChatPresencePublisher
{
    // Pub/Sub 전달은 영속 보장이나 Redis 상태 변경과의 단일 transaction을 뜻하지 않는다.
    Task PublishAsync(ChatPresenceChanged change, CancellationToken cancellationToken = default);
}
