using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Application.Ai;

public sealed class ChatAiProcessor(
    IChatAiStore store,
    IChatAiReplyClient client,
    IChatAiDelay delay,
    Func<double> randomRatio,
    Action<string, string> reportFailure)
{
    public async Task ProcessRequestedAsync(long messageId, CancellationToken cancellationToken)
    {
        IReadOnlyList<ChatAiPlan> plans;
        try
        {
            plans = await store.PlanAsync(messageId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            reportFailure("planning", exception.GetType().Name);
            return;
        }

        foreach (var plan in plans)
        {
            var prepared = await PrepareAsync(plan, cancellationToken);
            if (prepared.Response is null)
            {
                continue;
            }

            try
            {
                // 원본은 모델 호출이 끝난 뒤 저장/이벤트 전파만 지연한다.
                var settings = await store.ReadSettingsAsync(cancellationToken);
                if (settings.ResponseDelayEnabled && plan.Request.TriggerType is "MENTION" or "CONVERSATION")
                {
                    var wait = ChatAiPolicies.ResponseDelay(prepared.Response.Reply,
                        settings.ResponseDelayMinMillis, settings.ResponseDelayMaxMillis, randomRatio());
                    await delay.ScheduleAsync(plan, prepared.Response, wait, cancellationToken);
                }
                else
                {
                    await PersistAsync(plan, prepared.Response, null, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 원본 scheduler 실패 시 즉시 전달 fallback을 유지한다.
                reportFailure("delay", exception.GetType().Name);
                await PersistAsync(plan, prepared.Response, null, cancellationToken);
            }
        }
    }

    public async Task<ChatAiProcessingResult> ProcessPlanAsync(
        ChatAiPlan plan, ChatAiRevivalClaim? claim, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(plan, cancellationToken);
        return prepared.Response is null ? prepared.Result
            : await PersistAsync(plan, prepared.Response, claim, cancellationToken);
    }

    public async Task<ChatAiProcessingResult> PersistAsync(ChatAiPlan plan, ChatAiReplyResponse response,
        ChatAiRevivalClaim? claim, CancellationToken cancellationToken)
    {
        try
        {
            if (await store.ExistsReplyAsync(plan.Request.RequestId, cancellationToken))
            {
                return ChatAiProcessingResult.Duplicate;
            }

            return await store.SaveReplyAsync(plan, response.Reply!, claim, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            reportFailure("persistence", exception.GetType().Name);
            return ChatAiProcessingResult.Failed;
        }
    }

    public async Task ProcessDueAsync(DateTime batchNow, int limit, CancellationToken cancellationToken)
    {
        var ids = await store.FindDueAsync(batchNow, Math.Max(1, limit), cancellationToken);
        foreach (long id in ids)
        {
            var claim = await store.ClaimRevivalAsync(id, batchNow, cancellationToken);
            if (claim is null)
            {
                continue;
            }

            var result = ChatAiProcessingResult.Failed;
            TimeSpan? retryAfter = null;
            try
            {
                var plan = await store.PlanRevivalAsync(claim, cancellationToken);
                if (plan is not null)
                {
                    var prepared = await PrepareAsync(plan, cancellationToken);
                    retryAfter = prepared.RetryAfter;
                    result = prepared.Response is null ? prepared.Result
                        : await PersistAsync(plan, prepared.Response, claim, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                reportFailure("revival", exception.GetType().Name);
            }

            try
            {
                // 원본은 batch 시작 시각을 finish에도 전달한다. 완료 현재 시각으로 임의 변경하지 않는다.
                await store.FinishRevivalAsync(claim, result, batchNow, cancellationToken, retryAfter);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                reportFailure("revival-finish", exception.GetType().Name);
            }
        }
    }

    private async Task<(ChatAiReplyResponse? Response, ChatAiProcessingResult Result, TimeSpan? RetryAfter)> PrepareAsync(
        ChatAiPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            if (await store.ExistsReplyAsync(plan.Request.RequestId, cancellationToken))
            {
                return (null, ChatAiProcessingResult.Duplicate, null);
            }

            if (!client.IsConfigured)
            {
                throw new ChatMessageDependencyUnavailableException("AI chat reply client");
            }

            var response = await client.GenerateAsync(plan.Request, cancellationToken);
            if (response is null || response.RequestId != plan.Request.RequestId)
            {
                return (null, ChatAiProcessingResult.Failed, null);
            }

            if (!response.ShouldRespond)
            {
                return (null, ChatAiProcessingResult.Skipped, null);
            }

            if (ChatMessageText.IsBlank(response.Reply) || response.LanguageCode is null
                || !plan.Request.AiMember.OriginalLanguageCode.Equals(response.LanguageCode, StringComparison.OrdinalIgnoreCase))
            {
                return (null, ChatAiProcessingResult.Failed, null);
            }

            return (response, ChatAiProcessingResult.Responded, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ChatExecutionDeferredException deferred)
        {
            // Revival은 기존 실패 예약을 사용하되 Provider가 요구한 시각보다 빨리 재claim하지 않는다.
            reportFailure("preparation", deferred.GetType().Name);
            return (null, ChatAiProcessingResult.Failed, deferred.RetryAfter);
        }
        catch (Exception exception)
        {
            reportFailure("preparation", exception.GetType().Name);
            return (null, ChatAiProcessingResult.Failed, null);
        }
    }
}
