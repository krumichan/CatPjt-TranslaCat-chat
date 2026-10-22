using TranslaCat.Chat.Application.Core;

namespace TranslaCat.Chat.Infrastructure.Push;

public interface IOpenInvitationPushStore
{
    Task<IReadOnlyList<long>> CandidatesAsync(bool deliveries, CancellationToken token);
    Task<OpenInvitationPushOutboxEntity?> ClaimOutboxAsync(long id, CancellationToken token);
    Task<OpenInvitationPushDeliveryEntity?> ClaimDeliveryAsync(long id, CancellationToken token);
    Task<OpenInvitationPushOutboxEntity> OutboxAsync(long id, CancellationToken token);
    Task<bool> EligibleAsync(OpenInvitationPushOutboxEntity job, CancellationToken token);
    Task<bool> ExpandAsync(OpenInvitationPushOutboxEntity job, IReadOnlyList<ChatCorePushTarget> targets, CancellationToken token);
    Task<bool> FinishOutboxAsync(OpenInvitationPushOutboxEntity job, string status, int delaySeconds, CancellationToken token);
    Task<bool> FinishDeliveryAsync(OpenInvitationPushDeliveryEntity job, string status, int delaySeconds, string? ticketId, bool resetAttempts, CancellationToken token);
    Task<bool> RefreshTargetAsync(OpenInvitationPushDeliveryEntity job, ChatCorePushTarget target, CancellationToken token);
    Task<bool> OwnsDeliveryAsync(OpenInvitationPushDeliveryEntity job, CancellationToken token);
}
