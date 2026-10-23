using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Safety;
using TranslaCat.Chat.Infrastructure.Persistence;

namespace TranslaCat.Chat.Infrastructure.Safety;

public sealed class EfChatReportStore(IDbContextFactory<ChatDbContext> contexts,
    IChatMessageProfileReader? profiles, IChatContentPolicy? policy = null) : IChatReportStore
{
    public async Task<ChatReportReceipt> CreateAsync(long reporterUserId, long roomId, ChatReportCreateRequest request, DateTime now, CancellationToken token)
    {
        ChatReportPolicy.ValidateCreate(reporterUserId, roomId, request);
        if (policy is not null) await policy.RequireActiveAsync(reporterUserId, token);
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            { roomId, request.TargetType, request.MessageId, request.TargetOpenChatMemberId, request.TargetUserId, request.Reason, request.Detail }))));
        try
        {
            return await CreateWithinAsync(reporterUserId, roomId, request, fingerprint, now, token);
        }
        catch (DbUpdateException)
        {
            // 동일 UUID의 동시 첫 요청만 idempotent readback한다. 다른 DB 실패를 성공으로 감추지 않는다.
            await using var read = await contexts.CreateDbContextAsync(token);
            var duplicate = await read.ChatReports.AsNoTracking().SingleOrDefaultAsync(row => row.ReporterUserId == reporterUserId
                && row.ClientRequestId == request.ClientRequestId, token);
            if (duplicate is null) throw;
            return Duplicate(duplicate, fingerprint, now);
        }
    }

    private async Task<ChatReportReceipt> CreateWithinAsync(long reporterUserId, long roomId, ChatReportCreateRequest request,
        string fingerprint, DateTime now, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        if (policy is not null) await ChatAccountWriteGate.AcquireAsync(db, [reporterUserId], token);
        var duplicate = await db.ChatReports.AsNoTracking().SingleOrDefaultAsync(row => row.ReporterUserId == reporterUserId
            && row.ClientRequestId == request.ClientRequestId, token);
        if (duplicate is not null) return Duplicate(duplicate, fingerprint, now);

        // 신고자 membership을 잠그고 현재 OPEN ban과 대상을 같은 방/가입 기간으로 제한한다.
        var membership = await db.ChatRoomMembers.FromSqlInterpolated($"""
            SELECT * FROM chat_room_member WHERE chat_room_id={roomId} AND user_id={reporterUserId} FOR UPDATE
            """).SingleOrDefaultAsync(token);
        var room = await db.ChatRooms.AsNoTracking().SingleOrDefaultAsync(row => row.Id == roomId, token);
        if (membership is null || !membership.Active || membership.DeletedAt is not null || room is null
            || await db.OpenChatBans.AnyAsync(row => row.ChatRoomId == roomId && row.TargetUserId == reporterUserId && row.ReleasedAt == null, token))
            throw AccessDenied();

        long? targetUserId = null, targetMemberId = null;
        string? content = null, display = null;
        if (request.TargetType == "MESSAGE")
        {
            var message = await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(row => row.Id == request.MessageId
                && row.ChatRoomId == roomId && row.Status == "SENT" && row.DeletedAt == null
                && row.MessageType != "SYSTEM" && row.CreatedAt >= membership.JoinedAt, token) ?? throw AccessDenied();
            if (message.SenderType is not ("USER" or "AI") || message.SenderUserId == reporterUserId) throw AccessDenied();
            targetUserId = message.SenderUserId;
            content = message.Content;
            if (message.SenderType == "AI" && message.SenderAiMemberId is { } aiId)
                display = await (from member in db.ChatRoomAiMembers join agent in db.ChatAiAgents on member.AiAgentId equals agent.Id
                    where member.Id == aiId select agent.Nickname).FirstOrDefaultAsync(token) ?? "AI 참여자";
        }
        else
        {
            // OPEN membership ID를 실제 user ID로 바꾸는 과정은 서버 내부에서만 수행한다.
            if ((room.RoomType == "OPEN") != (request.TargetOpenChatMemberId is not null)) throw AccessDenied();
            var target = await db.ChatRoomMembers.AsNoTracking().SingleOrDefaultAsync(row => row.ChatRoomId == roomId
                && row.Active && row.DeletedAt == null
                && (request.TargetOpenChatMemberId != null ? row.Id == request.TargetOpenChatMemberId : row.UserId == request.TargetUserId), token)
                ?? throw AccessDenied();
            targetUserId = target.UserId;
            targetMemberId = target.Id;
            if (targetUserId == reporterUserId) throw AccessDenied();
        }

        if (targetUserId is { } user)
        {
            if (room.RoomType == "OPEN")
            {
                var open = await (from member in db.ChatRoomMembers join profile in db.OpenChatMemberProfiles on member.Id equals profile.ChatRoomMemberId
                    where member.ChatRoomId == roomId && member.UserId == user select new { member.Id, profile.Nickname }).SingleOrDefaultAsync(token);
                targetMemberId = open?.Id;
                display = open?.Nickname;
            }
            else if (await db.ChatAccountDeletions.AnyAsync(row => row.UserId == user, token)) display = "탈퇴한 사용자";
            else if (profiles is not null) display = (await profiles.GetUserAsync(user, token)).Name;
        }

        var report = new ChatReportEntity
        {
            RoomId = roomId, ReporterUserId = reporterUserId, ClientRequestId = request.ClientRequestId!, RequestFingerprint = fingerprint,
            TargetType = request.TargetType!, MessageId = request.MessageId, TargetUserId = targetUserId, TargetMemberId = targetMemberId,
            Reason = request.Reason!, Detail = request.Detail, EvidenceContent = content, EvidenceSenderDisplay = BoundedDisplay(display),
            CreatedAt = now, UpdatedAt = now, EvidenceExpiresAt = now.AddDays(ChatReportPolicy.EvidenceRetentionDays)
        };
        db.ChatReports.Add(report);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Receipt(report, now);
    }

    public async Task<ChatReportPage> ListAsync(string? status, long? cursorId, int size, DateTime now, CancellationToken token)
    {
        if ((status is not null && !ChatReportPolicy.Statuses.Contains(status)) || cursorId is <= 0 || size is < 1 or > 100)
            throw new ChatSafetyException("CHAT_REPORT_INVALID", "신고 목록 조건을 확인해 주세요.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.ChatReports.AsNoTracking().Where(row => (status == null || row.Status == status)
                && (cursorId == null || row.Id < cursorId)).OrderByDescending(row => row.Id).Take(size + 1).ToListAsync(token);
        var page = rows.Take(size).Select(row => Receipt(row, now)).ToArray();
        return new(page, rows.Count > size && page.Length > 0 ? page[^1].Id : null, rows.Count > size);
    }

    public async Task<ChatReportDetail> GetAsync(long reportId, DateTime now, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await db.ChatReports.AsNoTracking().SingleOrDefaultAsync(row => row.Id == reportId, token) ?? throw NotFound();
        return Detail(row, now);
    }

    public async Task<ChatReportDetail> UpdateAsync(long reviewerUserId, long reportId, ChatReportStatusRequest request, DateTime now, CancellationToken token)
    {
        ChatReportPolicy.ValidateStatus(request);
        if (policy is not null) await policy.RequireActiveAsync(reviewerUserId, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        if (policy is not null) await ChatAccountWriteGate.AcquireAsync(db, [reviewerUserId], token);
        var row = await db.ChatReports.FromSqlInterpolated($"SELECT * FROM chat_report WHERE id={reportId} FOR UPDATE")
            .SingleOrDefaultAsync(token) ?? throw NotFound();
        if (row.Version != request.ExpectedVersion) throw new ChatSafetyException("CHAT_REPORT_VERSION_CONFLICT", "다른 관리자가 수정했습니다. 다시 확인해 주세요.");
        if (row.EvidenceExpiresAt <= now && !string.IsNullOrEmpty(request.ReviewNote))
            throw new ChatSafetyException("CHAT_REPORT_EVIDENCE_EXPIRED", "증거 보관 기간이 끝나 새 처리 메모를 저장할 수 없습니다.");

        // 상태 변경만 기록한다. 신고 접수나 처리 상태 자체로 계정/메시지를 자동 제재하지 않는다.
        db.ChatReportReviews.Add(new() { ReportId = row.Id, ReviewerUserId = row.EvidenceExpiresAt > now ? reviewerUserId : null,
            PreviousStatus = row.Status, Status = request.Status!, Note = request.ReviewNote, CreatedAt = now });
        row.Status = request.Status!;
        row.ReviewNote = request.ReviewNote;
        row.Version++;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Detail(row, now);
    }

    public async Task PurgeExpiredEvidenceAsync(DateTime now, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var ids = await db.ChatReports.Where(row => row.EvidencePurgedAt == null && row.EvidenceExpiresAt <= now)
            .OrderBy(row => row.Id).Take(100).Select(row => row.Id).ToListAsync(token);
        if (ids.Count == 0) return;

        // 삭제가 늦어져도 Detail()은 기한부터 내용을 숨긴다. worker는 실제 저장된 자유 서술과 식별자도 지운다.
        await db.ChatReportReviews.Where(row => ids.Contains(row.ReportId)).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.ReviewerUserId, (long?)null).SetProperty(row => row.Note, (string?)null), token);
        await db.ChatReports.Where(row => ids.Contains(row.Id) && row.EvidencePurgedAt == null && row.EvidenceExpiresAt <= now)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.ReporterUserId, (long?)null)
                .SetProperty(row => row.TargetUserId, (long?)null).SetProperty(row => row.TargetMemberId, (long?)null)
                .SetProperty(row => row.MessageId, (long?)null).SetProperty(row => row.Detail, (string?)null)
                .SetProperty(row => row.EvidenceContent, (string?)null).SetProperty(row => row.EvidenceSenderDisplay, (string?)null)
                .SetProperty(row => row.ReviewNote, (string?)null).SetProperty(row => row.RequestFingerprint, "")
                .SetProperty(row => row.EvidencePurgedAt, now), token);
        await transaction.CommitAsync(token);
    }

    private static ChatReportReceipt Duplicate(ChatReportEntity report, string fingerprint, DateTime now)
    {
        if (report.RequestFingerprint != fingerprint) throw new ChatSafetyException("CHAT_REPORT_REQUEST_CONFLICT", "이미 사용한 신고 요청 번호입니다.");
        return Receipt(report, now) with { Duplicate = true };
    }
    private static string? BoundedDisplay(string? value) => value is null ? null : value[..Math.Min(value.Length, 150)];
    private static ChatReportReceipt Receipt(ChatReportEntity row, DateTime now) => new(row.Id, row.RoomId, row.TargetType, row.Reason,
        row.Status, row.CreatedAt, row.EvidenceExpiresAt, false, row.Version, row.EvidenceExpiresAt <= now || row.EvidencePurgedAt is not null);
    private static ChatReportDetail Detail(ChatReportEntity row, DateTime now)
    {
        var receipt = Receipt(row, now);
        return new(receipt, receipt.EvidenceExpired ? null : row.Detail, receipt.EvidenceExpired ? null : row.EvidenceContent,
            receipt.EvidenceExpired ? null : row.EvidenceSenderDisplay, receipt.EvidenceExpired ? null : row.ReviewNote, row.UpdatedAt);
    }
    private static ChatSafetyException AccessDenied() => new("CHAT_REPORT_TARGET_NOT_ACCESSIBLE", "이 방에서 신고할 수 있는 대상을 찾을 수 없습니다.");
    private static ChatSafetyException NotFound() => new("CHAT_REPORT_NOT_FOUND", "신고를 찾을 수 없습니다.");
}
