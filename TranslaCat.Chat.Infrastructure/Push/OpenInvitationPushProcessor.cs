using System.Globalization;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Infrastructure.Core;

namespace TranslaCat.Chat.Infrastructure.Push;

public sealed class OpenInvitationPushProcessor(IOpenInvitationPushStore store, HttpChatCoreClient core, ExpoChatPushClient expo, ChatPushOptions options, IChatNotificationStore? notifications = null)
{
    public async Task SweepAsync(CancellationToken token)
    {
        // 각 프로세스는 직렬·한정 batch로 호출한다. 다른 프로세스와의 중복 실행은 DB lease가 제한한다.
        foreach (long id in await store.CandidatesAsync(false, token))
        {
            var job = await store.ClaimOutboxAsync(id, token);
            if (job is not null) await ExpandAsync(job, token);
        }

        foreach (long id in await store.CandidatesAsync(true, token))
        {
            var job = await store.ClaimDeliveryAsync(id, token);
            if (job is not null) await DeliverAsync(job, token);
        }
    }

    private async Task ExpandAsync(OpenInvitationPushOutboxEntity job, CancellationToken token)
    {
        if (job.Attempts > options.MaxSendAttempts)
        {
            await store.FinishOutboxAsync(job, "FAILED", 0, token);
            return;
        }

        try
        {
            if (!await EligibleAsync(job, token))
            {
                await store.FinishOutboxAsync(job, "SUPPRESSED", 0, token);
                return;
            }

            var targets = await core.PushTargetsAsync([job.RecipientUserId], options.AppEnvironment!, options.ProjectId!, token);
            await store.ExpandAsync(job, targets.Targets, token);
        }
        catch (ChatCoreUnavailableException)
        {
            await store.FinishOutboxAsync(job, job.Attempts >= options.MaxSendAttempts ? "FAILED" : "PENDING", Backoff(job.Attempts), token);
        }
    }

    private async Task DeliverAsync(OpenInvitationPushDeliveryEntity job, CancellationToken token)
    {
        int limit = job.Status == "RECEIPT" ? options.MaxReceiptAttempts : options.MaxSendAttempts;
        if (job.Attempts > limit)
        {
            await store.FinishDeliveryAsync(job, "FAILED", 0, job.TicketId, false, token);
            return;
        }

        try
        {
            // DeviceNotRegistered는 먼저 영속 상태로 남긴 뒤 별도 재시도한다. 갱신된 설치는 BE의 네 값 비교가 보호한다.
            if (job.Status == "INVALIDATE")
            {
                if (!await store.OwnsDeliveryAsync(job, token)) return;
                await core.InvalidatePushAsync(job.InstallationId, job.AccountBindingId, job.Generation, job.TokenVersion, token);
                await store.FinishDeliveryAsync(job, "INVALIDATED", 0, null, false, token);
                return;
            }

            if (job.Status == "RECEIPT")
            {
                var receipt = await expo.ReceiptAsync(job.TicketId!, token);
                await ApplyResultAsync(job, receipt, true, token);
                return;
            }

            var outbox = await store.OutboxAsync(job.OutboxId, token);

            // off/mute 작업에는 요약 조회도 하지 않는다. badge는 수신자당 한 번 읽고 그 뒤 binding·정책을 재확인한다.
            if (!await store.EligibleAsync(outbox, token))
            {
                await store.FinishDeliveryAsync(job, "SUPPRESSED", 0, null, false, token);
                return;
            }
            int? badge = await ChatPushBadge.ReadAsync(notifications, outbox.RecipientUserId, token);
            var targets = await core.PushTargetsAsync([outbox.RecipientUserId], options.AppEnvironment!, options.ProjectId!, token);
            var target = targets.Targets.SingleOrDefault(value => Matches(job, value));
            if (target is null || !await EligibleAsync(outbox, token))
            {
                await store.FinishDeliveryAsync(job, "SUPPRESSED", 0, null, false, token);
                return;
            }

            // 마지막 정책 조회 뒤 lease 소유권을 재확인한다. 이미 provider에 넘긴 초대는 회수할 수 없다.
            // ticket 저장 전 프로세스가 종료되면 재전송될 수 있다. eventId는 유지하며 정확히 한 번 전달을 주장하지 않는다.
            if (!await store.RefreshTargetAsync(job, target, token) || !await store.OwnsDeliveryAsync(job, token)) return;
            var payload = Payload(outbox, target) with { Badge = badge };
            var result = await expo.SendAsync(payload, token);
            await ApplyResultAsync(job, result, false, token);
        }
        catch (ChatCoreUnavailableException)
        {
            await RetryAsync(job, token);
        }
    }

    private async Task<bool> EligibleAsync(OpenInvitationPushOutboxEntity job, CancellationToken token)
    {
        if (!await store.EligibleAsync(job, token)) return false;

        // 양방향 block은 BE의 기존 관계 판정에 맡긴다. 누락 응답은 허용으로 해석하지 않는다.
        var relations = await core.RelationsAsync(job.RecipientUserId, [job.SenderUserId], token);
        if (relations.Relations is null || relations.Relations.Count != 1 || relations.MissingUserIds is null
            || relations.MissingUserIds.Count != 0 || relations.Relations[0] is null
            || relations.Relations[0].TargetUserId != job.SenderUserId) throw new ChatCoreUnavailableException();
        var relation = relations.Relations[0];
        return !relation.Blocked;
    }

    private async Task ApplyResultAsync(OpenInvitationPushDeliveryEntity job, ExpoPushResult result, bool receipt, CancellationToken token)
    {
        if (result.Status == "ok")
        {
            await store.FinishDeliveryAsync(job, receipt ? "RECEIPT_OK" : "RECEIPT",
                receipt ? 0 : options.ReceiptDelaySeconds, receipt ? null : result.TicketId, !receipt, token);
        }
        else if (result.Error == "DeviceNotRegistered")
        {
            await store.FinishDeliveryAsync(job, "INVALIDATE", 0, null, true, token);
        }
        else if (result.Status == "pending" || result.Error is "retry" or "MessageRateExceeded")
        {
            await RetryAsync(job, token);
        }
        else
        {
            await store.FinishDeliveryAsync(job, "FAILED", 0, null, false, token);
        }
    }

    private Task<bool> RetryAsync(OpenInvitationPushDeliveryEntity job, CancellationToken token)
    {
        int limit = job.Status == "RECEIPT" ? options.MaxReceiptAttempts : options.MaxSendAttempts;
        int delay = job.Status == "RECEIPT" ? options.ReceiptDelaySeconds : Backoff(job.Attempts);
        return store.FinishDeliveryAsync(job, job.Attempts >= limit ? "FAILED" : job.Status,
            delay, job.TicketId, false, token);
    }

    public static int Backoff(int attempt) => Math.Min(900, 5 * (1 << Math.Clamp(attempt - 1, 0, 8)));

    public static bool Matches(OpenInvitationPushDeliveryEntity job, ChatCorePushTarget target) =>
        job.InstallationId == target.InstallationId && job.AccountBindingId == target.AccountBindingId
        && job.Generation <= target.Generation && job.TokenVersion == target.TokenVersion;

    public static ExpoOpenInvitationPushPayload Payload(OpenInvitationPushOutboxEntity job, ChatCorePushTarget target) => new(
        target.ExpoPushToken, "TranslaCat", "오픈채팅 초대가 도착했어요.", "chat-messages", "default",
        new ExpoOpenInvitationPushData(2, "OPEN_INVITATION", job.EventId,
            job.NotificationId.ToString(CultureInfo.InvariantCulture), job.RoomId.ToString(CultureInfo.InvariantCulture), target.AccountBindingId));
}
