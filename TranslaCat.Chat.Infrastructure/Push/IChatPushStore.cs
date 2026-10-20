using TranslaCat.Chat.Application.Core;

namespace TranslaCat.Chat.Infrastructure.Push;

public interface IChatPushStore
{
    Task<IReadOnlyList<long>> CandidatesAsync(bool deliveries, CancellationToken token);
    Task<ChatPushOutboxEntity?> ClaimOutboxAsync(long id, CancellationToken token);
    Task<ChatPushDeliveryEntity?> ClaimDeliveryAsync(long id, CancellationToken token);
    Task<ChatPushOutboxEntity> OutboxAsync(long id, CancellationToken token);
    Task<bool> EligibleAsync(ChatPushOutboxEntity job, CancellationToken token);
    Task<bool> ExpandAsync(ChatPushOutboxEntity job, IReadOnlyList<ChatCorePushTarget> targets, CancellationToken token);
    Task<bool> FinishOutboxAsync(ChatPushOutboxEntity job, string status, int delaySeconds, CancellationToken token);
    Task<bool> FinishDeliveryAsync(ChatPushDeliveryEntity job, string status, int delaySeconds, string? ticketId, bool resetAttempts, CancellationToken token);
    Task<bool> RefreshTargetAsync(ChatPushDeliveryEntity job, ChatCorePushTarget target, CancellationToken token);
    Task<bool> OwnsDeliveryAsync(ChatPushDeliveryEntity job, CancellationToken token);
}
