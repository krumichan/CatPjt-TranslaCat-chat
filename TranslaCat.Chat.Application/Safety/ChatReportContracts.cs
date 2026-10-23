using System.Text.Json.Serialization;

namespace TranslaCat.Chat.Application.Safety;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChatReportCreateRequest([property: JsonRequired] string? TargetType, long? MessageId, long? TargetOpenChatMemberId,
    long? TargetUserId, [property: JsonRequired] string? Reason, string? Detail, [property: JsonRequired] string? ClientRequestId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChatReportStatusRequest([property: JsonRequired] string? Status, string? ReviewNote, [property: JsonRequired] int ExpectedVersion);
public sealed record ChatReportReceipt(long Id, long RoomId, string TargetType, string Reason, string Status,
    DateTime CreatedAt, DateTime EvidenceExpiresAt, bool Duplicate = false, int Version = 0, bool EvidenceExpired = false);
public sealed record ChatReportPage(IReadOnlyList<ChatReportReceipt> Reports, long? NextCursorId, bool HasNext);
public sealed record ChatReportDetail(ChatReportReceipt Receipt, string? Detail, string? EvidenceContent,
    string? EvidenceSenderDisplay, string? ReviewNote, DateTime UpdatedAt);

public interface IChatReportStore
{
    Task<ChatReportReceipt> CreateAsync(long reporterUserId, long roomId, ChatReportCreateRequest request, DateTime now, CancellationToken token);
    Task<ChatReportPage> ListAsync(string? status, long? cursorId, int size, DateTime now, CancellationToken token);
    Task<ChatReportDetail> GetAsync(long reportId, DateTime now, CancellationToken token);
    Task<ChatReportDetail> UpdateAsync(long reviewerUserId, long reportId, ChatReportStatusRequest request, DateTime now, CancellationToken token);
    Task PurgeExpiredEvidenceAsync(DateTime now, CancellationToken token);
}

public static class ChatReportPolicy
{
    public static readonly IReadOnlySet<string> Reasons = new HashSet<string>(StringComparer.Ordinal)
        { "SPAM", "HARASSMENT", "HATE", "SEXUAL", "ILLEGAL", "PERSONAL_INFORMATION", "OTHER" };
    public static readonly IReadOnlySet<string> Statuses = new HashSet<string>(StringComparer.Ordinal)
        { "PENDING", "IN_REVIEW", "RESOLVED", "DISMISSED" };
    public const int EvidenceRetentionDays = 90;

    public static void ValidateCreate(long userId, long roomId, ChatReportCreateRequest request)
    {
        // 대상 종류마다 한 식별자만 허용한다. OPEN 실제 계정 ID를 client가 지정할 수 없다.
        bool targetValid = request.TargetType switch
        {
            "MESSAGE" => request.MessageId is > 0 && request.TargetOpenChatMemberId is null && request.TargetUserId is null,
            "MEMBER" => request.MessageId is null && ((request.TargetOpenChatMemberId is > 0 && request.TargetUserId is null)
                || (request.TargetUserId is > 0 && request.TargetOpenChatMemberId is null)),
            _ => false
        };
        if (userId <= 0 || roomId <= 0 || !targetValid || request.Reason is null || !Reasons.Contains(request.Reason)
            || request.Detail is { Length: > 2000 } || request.Detail?.Any(value => char.IsControl(value) && value is not ('\r' or '\n' or '\t')) == true
            || !Guid.TryParseExact(request.ClientRequestId, "D", out var id) || id.ToString("D") != request.ClientRequestId)
            throw new ChatSafetyException("CHAT_REPORT_INVALID", "신고 대상과 사유를 확인해 주세요.");
    }

    public static void ValidateStatus(ChatReportStatusRequest request)
    {
        if (request.Status is null || !Statuses.Contains(request.Status) || request.ExpectedVersion < 0
            || request.ReviewNote is { Length: > 2000 }
            || request.ReviewNote?.Any(value => char.IsControl(value) && value is not ('\r' or '\n' or '\t')) == true)
            throw new ChatSafetyException("CHAT_REPORT_INVALID", "신고 처리 상태를 확인해 주세요.");
    }
}
