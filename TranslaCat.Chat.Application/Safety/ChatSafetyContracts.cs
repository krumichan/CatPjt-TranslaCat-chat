using System.Text.Json.Serialization;

namespace TranslaCat.Chat.Application.Safety;

public sealed record ChatAuthorVisibility(bool Blocked, bool AuthorDeleted);

public interface IChatContentPolicy
{
    // 실제 계정 관계는 BE에 맡기고, 탈퇴 tombstone은 CHAT의 늦은 요청을 차단한다.
    Task RequireActiveAsync(long userId, CancellationToken token);
    Task<IReadOnlyDictionary<long, ChatAuthorVisibility>> ResolveAsync(
        long userId, IReadOnlyList<long> senderIds, CancellationToken token);
    Task<IReadOnlySet<long>> BlockedSendersAsync(long userId, CancellationToken token);
}

public sealed class ChatSafetyException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChatAccountDeletionRequest([property: JsonRequired] string? OperationId,
    [property: JsonRequired] DateTimeOffset RequestedAtUtc, string? AuditIdentity);
public sealed record ChatAccountDeletionResult(string OperationId, long UserId, string Status, DateTimeOffset? CompletedAtUtc);

public interface IChatAccountDeletionStore
{
    Task<ChatAccountDeletionResult> DeleteAsync(long userId, ChatAccountDeletionRequest request, CancellationToken token);
}

public static class ChatAccountDeletionPolicy
{
    public static void Validate(long userId, ChatAccountDeletionRequest request)
    {
        // 업무 입력과 서비스 인증을 별도로 검사하며 민감 감사값은 오류에 포함하지 않는다.
        if (userId <= 0 || !Guid.TryParseExact(request.OperationId, "D", out var operation)
            || operation.ToString("D") != request.OperationId || request.RequestedAtUtc == default
            || request.RequestedAtUtc.Offset != TimeSpan.Zero
            || request.AuditIdentity is { Length: > 320 }
            || request.AuditIdentity?.Any(char.IsControl) == true)
        {
            throw new ChatSafetyException("CHAT_ACCOUNT_DELETION_INVALID", "탈퇴 정리 요청을 확인해 주세요.");
        }
    }
}
