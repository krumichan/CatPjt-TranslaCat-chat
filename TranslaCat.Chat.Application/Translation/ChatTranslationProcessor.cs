using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Application.Translation;

public sealed class ChatTranslationProcessor(
    IChatTranslationStore store,
    IChatTranslationClient client,
    IChatTranslationEventDelivery events,
    ChatTranslationOptions options,
    Func<DateTime> readClock,
    Action<string, string> reportDeliveryFailure)
{
    public async Task ProcessRequestedAsync(ChatTranslationRequestedIntent intent, CancellationToken cancellationToken)
    {
        foreach (long id in intent.TranslationIds.Distinct())
        {
            await ProcessOneAsync(id, intent.MessageId, false, cancellationToken);
        }
    }

    public async Task<ChatTranslationBatchResult> RetryFailedAsync(int? limit, CancellationToken cancellationToken)
    {
        return await SweepAsync("FAILED", NormalizeLimit(limit), true, cancellationToken);
    }

    public async Task<ChatTranslationBatchResult> RecoverPendingAsync(int? limit, CancellationToken cancellationToken)
    {
        // 새 복구 경로다. commit 뒤 in-process 통지가 유실되어도 DB의 PENDING 작업을 다시 발견한다.
        return await SweepAsync("PENDING", NormalizeLimit(limit), false, cancellationToken);
    }

    public async Task<ChatTranslationProcessResult> ProcessOneAsync(
        long translationId, long? expectedMessageId, bool allowFailed, CancellationToken cancellationToken)
    {
        if (!client.IsConfigured)
        {
            throw new ChatMessageDependencyUnavailableException("AI chat translation client");
        }

        var claim = await store.TryClaimAsync(translationId, expectedMessageId, allowFailed, options.LeaseDuration, cancellationToken);
        if (claim is null)
        {
            return ChatTranslationProcessResult.Skipped;
        }

        string? translated = null;
        string? failure = null;
        TimeSpan? retryAfter = null;
        try
        {
            // 외부 호출은 claim commit 이후, 결과 저장 transaction 이전에 실행한다.
            translated = await client.TranslateAsync(claim.Text, claim.LanguageCode, cancellationToken);
            if (ChatMessageText.IsBlank(translated))
            {
                throw new InvalidOperationException("AI translation response is empty.");
            }

            translated = ChatMessageText.Trim(translated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 수명 종료/요청 취소를 번역 실패로 위장하지 않는다. lease 만료 후 복구가 가능하다.
            throw;
        }
        catch (ChatExecutionDeferredException deferred)
        {
            // 실패 상태는 유지하되 자동 sweep의 다음 claim을 Provider 대기 지시 이후로 미룬다.
            failure = deferred.Message;
            retryAfter = deferred.RetryAfter;
        }
        catch (Exception exception)
        {
            var reason = ChatMessageText.IsBlank(exception.Message) ? exception.GetType().Name : exception.Message;
            failure = reason.Length <= 1000 ? reason : reason[..1000];
        }

        var change = await store.TryFinishAsync(claim, translated, failure, readClock(), cancellationToken, retryAfter);
        if (change is null)
        {
            return ChatTranslationProcessResult.Skipped;
        }

        try
        {
            await events.DeliverAsync(change, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            reportDeliveryFailure(change.Status, exception.GetType().Name);
        }

        return failure is null ? ChatTranslationProcessResult.Completed : ChatTranslationProcessResult.Failed;
    }

    private async Task<ChatTranslationBatchResult> SweepAsync(
        string status, int limit, bool allowFailed, CancellationToken cancellationToken)
    {
        var ids = await store.FindCandidatesAsync(status, limit, cancellationToken);
        int success = 0;
        int failed = 0;
        int skipped = 0;
        foreach (long id in ids)
        {
            var result = await ProcessOneAsync(id, null, allowFailed, cancellationToken);
            if (result == ChatTranslationProcessResult.Completed)
            {
                success++;
            }
            else if (result == ChatTranslationProcessResult.Failed)
            {
                failed++;
            }
            else
            {
                skipped++;
            }
        }

        return new(ids.Count, success, failed, skipped);
    }

    private static int NormalizeLimit(int? limit)
    {
        return limit is null or <= 0 ? 50 : Math.Min(limit.Value, 100);
    }
}
