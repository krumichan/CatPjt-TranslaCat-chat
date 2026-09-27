namespace TranslaCat.Chat.Application.Ai;

public sealed record ChatAiRoom(long RoomId, string RoomType, string? Name, string? Description);
public sealed record ChatAiMember(long AiMemberId, string Nickname, string? Bio, string? PersonaPrompt, string OriginalLanguageCode);
public sealed record ChatAiTriggerMessage(long MessageId, string SenderId, string SenderName, string Content, DateTime CreatedAt);
public sealed record ChatAiContextMessage(long MessageId, string SenderType, string? SenderId, string? SenderName, string Content, DateTime CreatedAt);
public sealed record ChatAiReplyRequest(string RequestId, string TriggerType, ChatAiRoom Room, ChatAiMember AiMember,
    ChatAiTriggerMessage? TriggerMessage, IReadOnlyList<ChatAiContextMessage> ContextMessages,
    int ContextMaxMessages, int ContextMaxCharacters, int ReplyMaxCharacters);
public sealed record ChatAiReplyResponse(string RequestId, bool ShouldRespond, string? Reply, string? LanguageCode);
public sealed record ChatAiPlan(long AiMemberId, ChatAiReplyRequest Request);
public sealed record ChatAiRevivalClaim(long ActivityId, long RoomId, long AiMemberId, string ClaimToken,
    long CycleVersion, int AttemptNumber, string RequestId);

public enum ChatAiProcessingResult
{
    Responded, Duplicate, Skipped, Failed
}

public interface IChatAiReplyClient
{
    bool IsConfigured
    {
        get;
    }
    Task<ChatAiReplyResponse?> GenerateAsync(ChatAiReplyRequest request, CancellationToken cancellationToken);
}

public interface IChatAiUserNameReader
{
    // 일반 profile displayName/email과 BE User.username을 같다고 가정하지 않는다.
    Task<string?> GetUserNameAsync(long userId, CancellationToken cancellationToken);
}

public sealed class ChatAiOptions
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
    public string BatchTimeZone { get; init; } = "Asia/Tokyo";
    public int RevivalLimit { get; init; } = 50;
    public TimeSpan RevivalInitialDelay { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan RevivalInterval { get; init; } = TimeSpan.FromSeconds(60);
    public int RevivalClaimTimeoutSeconds { get; init; } = 120;
    public int RevivalFailureRetryMinutes { get; init; } = 5;

    public bool IsConfigured => Enabled && AiBaseUri is { IsAbsoluteUri: true }
        && AiBaseUri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(ApiKey);

    public void Validate()
    {
        if (AttemptTimeout <= TimeSpan.Zero || AttemptTimeout > TimeSpan.FromSeconds(300)
            || ConnectTimeout <= TimeSpan.Zero || ConnectTimeout > AttemptTimeout
            || MaximumAttempts is < 1 or > 3 || RetryWait < TimeSpan.Zero
            || RevivalInitialDelay < TimeSpan.Zero || RevivalInterval <= TimeSpan.Zero
            || (ApiKey is not null && ApiKey.Any(char.IsControl))
            || (AiBaseUri is not null && (!AiBaseUri.IsAbsoluteUri || AiBaseUri.Scheme is not ("http" or "https")
                || AiBaseUri.UserInfo.Length > 0 || AiBaseUri.Query.Length > 0 || AiBaseUri.Fragment.Length > 0)))
        {
            throw new ArgumentException("Chat AI 실행 설정이 올바르지 않습니다.");
        }

        _ = TimeZoneInfo.FindSystemTimeZoneById(BatchTimeZone);
    }
}
