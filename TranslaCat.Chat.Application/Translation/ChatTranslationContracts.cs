using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Application.Translation;

public sealed record ChatTranslationClaim(
    long TranslationId, long ChatRoomId, long MessageId, string Token, string Text, string LanguageCode);

public sealed record ChatTranslationChanged(
    long ChatRoomId, long MessageId, long TranslationId, string LanguageCode,
    string Status, string? TranslatedContent, string? FailureReason);

public sealed record ChatTranslationRetryChange(
    ChatMessageTranslationView Translation, ChatTranslationRequestedIntent? Intent);

public enum ChatTranslationProcessResult
{
    Skipped, Completed, Failed
}

public sealed record ChatTranslationBatchResult(int TargetCount, int SuccessCount, int FailedCount, int SkippedCount);

public interface IChatTranslationStore
{
    Task<IReadOnlyList<long>> FindCandidatesAsync(string status, int limit, CancellationToken cancellationToken);
    Task<ChatTranslationClaim?> TryClaimAsync(long translationId, long? expectedMessageId,
        bool allowFailed, TimeSpan leaseDuration, CancellationToken cancellationToken);

    // token과 DB 시계 기준 lease를 다시 확인한다. 반환 시점에는 결과 저장 commit이 완료되어야 한다.
    Task<ChatTranslationChanged?> TryFinishAsync(ChatTranslationClaim claim, string? translatedContent,
        string? failureReason, DateTime completedAt, CancellationToken cancellationToken,
        TimeSpan? retryAfter = null);
    Task<ChatTranslationRetryChange> RetryAsync(long userId, long roomId, long messageId,
        string languageCode, bool processingAvailable, DateTime changedAt, CancellationToken cancellationToken);
}

public interface IChatTranslationClient
{
    bool IsConfigured
    {
        get;
    }
    Task<string> TranslateAsync(string text, string targetLanguageCode, CancellationToken cancellationToken);
}

public interface IChatTranslationEventDelivery
{
    Task DeliverAsync(ChatTranslationChanged change, CancellationToken cancellationToken);
}

public sealed class ChatTranslationOptions
{
    public bool Enabled
    {
        get; init;
    }
    public Uri? AiBaseUri
    {
        get; init;
    }
    public string? ApiKey
    {
        get; init;
    }
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(300);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumAttempts { get; init; } = 3;
    public TimeSpan RetryWait { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(1020);
    public int RetryLimit { get; init; } = 50;
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(30);

    public bool IsConfigured => Enabled && AiBaseUri is { IsAbsoluteUri: true }
        && AiBaseUri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(ApiKey);

    public void Validate()
    {
        // 재시도 전체 상한보다 lease가 길어야 정상 실행 중 다른 worker가 다시 claim하지 않는다.
        if (MaximumAttempts is < 1 or > 3 || AttemptTimeout <= TimeSpan.Zero
            || AttemptTimeout > TimeSpan.FromSeconds(300) || ConnectTimeout <= TimeSpan.Zero
            || ConnectTimeout > AttemptTimeout
            || RetryWait < TimeSpan.Zero || SweepInterval <= TimeSpan.Zero || InitialDelay < TimeSpan.Zero
            || (ApiKey is not null && ApiKey.Any(char.IsControl))
            || LeaseDuration <= (AttemptTimeout * MaximumAttempts) + (RetryWait * (MaximumAttempts - 1))
            || (AiBaseUri is not null && (!AiBaseUri.IsAbsoluteUri || AiBaseUri.Scheme is not ("http" or "https")
                || AiBaseUri.UserInfo.Length > 0 || AiBaseUri.Query.Length > 0 || AiBaseUri.Fragment.Length > 0)))
        {
            throw new ArgumentException("번역 timeout/retry/lease 또는 AI endpoint 설정이 올바르지 않습니다.");
        }
    }
}
