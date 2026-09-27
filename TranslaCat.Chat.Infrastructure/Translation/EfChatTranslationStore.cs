using System.Data;
using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Translation;
using TranslaCat.Chat.Infrastructure.Persistence;

namespace TranslaCat.Chat.Infrastructure.Translation;

public sealed class EfChatTranslationStore(
    IDbContextFactory<ChatDbContext> contexts,
    Func<DateTime> readClock,
    IChatMessageProfileReader? profiles = null) : IChatTranslationStore
{
    public async Task<IReadOnlyList<long>> FindCandidatesAsync(
        string status, int limit, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);

        // DB 시계를 기준으로 만료된 lease만 선택한다. 최종 경쟁 판정은 조건부 UPDATE가 담당한다.
        var rows = await context.ChatMessageTranslations.FromSqlInterpolated($"""
            SELECT * FROM chat_message_translation
            WHERE status = {status} AND deleted_at IS NULL
              AND (processing_expires_at IS NULL OR processing_expires_at <= UTC_TIMESTAMP(6))
            ORDER BY id ASC LIMIT {limit}
            """).AsNoTracking().ToListAsync(cancellationToken);
        return rows.Select(row => row.Id).ToArray();
    }

    public async Task<ChatTranslationClaim?> TryClaimAsync(
        long translationId, long? expectedMessageId, bool allowFailed,
        TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        string token = Guid.NewGuid().ToString("D");
        long leaseMicroseconds = checked(leaseDuration.Ticks / 10);
        DateTime changedAt = readClock();

        // 외부 호출 전에 짧은 transaction으로 소유권을 얻는다. 서로 다른 worker도 같은 token을 얻지 못한다.
        int changed = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE chat_message_translation
            SET status = 'PENDING', failure_reason = NULL, completed_at = NULL,
                processing_token = {token},
                processing_expires_at = TIMESTAMPADD(MICROSECOND, {leaseMicroseconds}, UTC_TIMESTAMP(6)),
                updated_by = 'SYSTEM', updated_at = {changedAt}
            WHERE id = {translationId} AND deleted_at IS NULL
              AND (status = 'PENDING' OR ({allowFailed} AND status = 'FAILED'))
              AND ({expectedMessageId} IS NULL OR chat_message_id = {expectedMessageId})
              AND (processing_expires_at IS NULL OR processing_expires_at <= UTC_TIMESTAMP(6))
            """, cancellationToken);
        if (changed == 0)
        {
            return null;
        }

        var claim = await (from translation in context.ChatMessageTranslations.AsNoTracking()
                           join message in context.ChatMessages.AsNoTracking() on translation.ChatMessageId equals message.Id
                           where translation.Id == translationId && translation.ProcessingToken == token
                           select new ChatTranslationClaim(translation.Id, message.ChatRoomId, message.Id,
                               token, message.Content, translation.LanguageCode)).SingleAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claim;
    }

    public async Task<ChatTranslationChanged?> TryFinishAsync(
        ChatTranslationClaim claim, string? translatedContent, string? failureReason,
        DateTime completedAt, CancellationToken cancellationToken, TimeSpan? retryAfter = null)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        bool succeeded = failureReason is null;
        string status = succeeded ? "COMPLETED" : "FAILED";
        DateTime? storedCompletedAt = succeeded ? completedAt : null;
        int? retryDelaySeconds = retryAfter is { } wait ? checked((int)Math.Ceiling(wait.TotalSeconds)) : null;

        // lease가 만료되거나 다른 실행이 인계받았다면 늦게 도착한 결과를 덮어쓰지 않는다.
        // 원본 fail/retry는 기존 translated_content를 지우지 않는다.
        // FAILED의 만료 시각은 자동 sweep의 재claim 금지 시각으로도 사용한다.
        int changed = await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE chat_message_translation
            SET status = {status},
                translated_content = CASE WHEN {succeeded} THEN {translatedContent} ELSE translated_content END,
                failure_reason = {failureReason}, completed_at = {storedCompletedAt},
                processing_token = NULL,
                processing_expires_at = CASE WHEN {retryDelaySeconds} IS NULL THEN NULL
                    ELSE TIMESTAMPADD(SECOND, {retryDelaySeconds}, UTC_TIMESTAMP(6)) END,
                updated_at = {completedAt}, updated_by = 'SYSTEM'
            WHERE id = {claim.TranslationId} AND processing_token = {claim.Token}
              AND status = 'PENDING' AND deleted_at IS NULL
              AND processing_expires_at > UTC_TIMESTAMP(6)
            """, cancellationToken);
        if (changed == 0)
        {
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return new ChatTranslationChanged(claim.ChatRoomId, claim.MessageId, claim.TranslationId,
            claim.LanguageCode, status, succeeded ? translatedContent : null, failureReason);
    }

    public async Task<ChatTranslationRetryChange> RetryAsync(
        long userId, long roomId, long messageId, string languageCode, bool processingAvailable,
        DateTime changedAt, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);

        // 동일/완료 상태여도 원본처럼 OPEN 접근, membership, 대상 메시지 검증을 먼저 수행한다.
        await ValidateAccessAsync(context, userId, roomId, cancellationToken);
        var message = await context.ChatMessages.AsNoTracking().SingleOrDefaultAsync(
            row => row.Id == messageId && row.ChatRoomId == roomId && row.DeletedAt == null,
            cancellationToken) ?? throw new ChatMessageException("채팅 메시지를 찾을 수 없습니다.");
        var rows = await context.ChatMessageTranslations.FromSqlInterpolated($"""
            SELECT * FROM chat_message_translation
            WHERE chat_message_id = {messageId} AND language_code = {languageCode} AND deleted_at IS NULL
            FOR UPDATE
            """).ToListAsync(cancellationToken);
        var translation = rows.SingleOrDefault()
            ?? throw new ChatMessageException("재시도할 번역 정보를 찾을 수 없습니다.");

        ChatTranslationRequestedIntent? intent = null;
        if (translation.Status == "FAILED")
        {
            if (!processingAvailable)
            {
                throw new ChatMessageDependencyUnavailableException("chat translation dispatcher");
            }

            // 자동 claim과 같은 행 잠금을 사용하고, commit 후 통지가 유실되면 PENDING sweep이 복구한다.
            translation.Status = "PENDING";
            translation.FailureReason = null;
            translation.CompletedAt = null;
            translation.ProcessingToken = null;
            translation.ProcessingExpiresAt = null;
            translation.UpdatedAt = changedAt;
            var identity = profiles ?? throw new ChatMessageDependencyUnavailableException("account audit identity");
            translation.UpdatedBy = await identity.GetAuditIdentityAsync(userId, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            intent = new(roomId, messageId, message.SenderUserId, Array.AsReadOnly(new[] { translation.Id }));
        }

        var result = new ChatMessageTranslationView(translation.Id, translation.LanguageCode,
            translation.TranslatedContent, translation.Status, translation.FailureReason, translation.CompletedAt);
        await transaction.CommitAsync(cancellationToken);
        return new(result, intent);
    }

    private static async Task ValidateAccessAsync(
        ChatDbContext context, long userId, long roomId, CancellationToken cancellationToken)
    {
        bool openRoom = await context.OpenChatRooms.AnyAsync(row => row.ChatRoomId == roomId, cancellationToken);
        if (openRoom && await context.OpenChatBans.AnyAsync(row => row.ChatRoomId == roomId
            && row.TargetUserId == userId && row.ReleasedAt == null, cancellationToken))
        {
            throw new ChatMessageException("해당 OPEN 채팅방에서 차단되어 접근할 수 없습니다.", "OPEN_CHAT_BANNED");
        }

        var roomType = await (from member in context.ChatRoomMembers.AsNoTracking()
                              join room in context.ChatRooms.AsNoTracking() on member.ChatRoomId equals room.Id
                              where member.ChatRoomId == roomId && member.UserId == userId
                                  && member.Active && member.DeletedAt == null
                              select room.RoomType).SingleOrDefaultAsync(cancellationToken);
        if (roomType is null)
        {
            throw new ChatMessageException(openRoom ? "OPEN 채팅방 멤버가 아니거나 접근 권한이 없습니다."
                : "채팅방 멤버가 아니거나 접근 권한이 없습니다.",
                openRoom ? "OPEN_CHAT_MEMBER_ACCESS_DENIED" : "CHAT_ROOM_MEMBER_ACCESS_DENIED");
        }

        if (openRoom && roomType != "OPEN")
        {
            throw new ChatMessageException("OPEN 채팅방을 찾을 수 없습니다.", "OPEN_CHAT_ROOM_NOT_FOUND");
        }
    }
}
