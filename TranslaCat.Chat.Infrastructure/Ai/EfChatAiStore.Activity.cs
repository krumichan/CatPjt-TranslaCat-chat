using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Ai;

public sealed partial class EfChatAiStore
{
    public async Task RecordHumanAsync(ChatHumanMessageRecordedIntent intent, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var room = await LockRoomAsync(db, intent.ChatRoomId, cancellationToken);
        if (room is null || !room.Active || room.DeletedAt is not null || room.RoomType is not ("GROUP" or "OPEN"))
        {
            return;
        }

        var settings = await SettingsWithinAsync(db, cancellationToken);
        var rows = await db.ChatRoomAiActivities.FromSqlInterpolated(
            $"SELECT * FROM chat_room_ai_activity WHERE chat_room_id = {room.Id} FOR UPDATE").ToListAsync(cancellationToken);
        var activity = rows.SingleOrDefault();
        bool newer = activity?.LastHumanMessageId is { } lastId
            ? intent.MessageId > lastId
            : activity?.LastHumanMessageAt is null || intent.CreatedAt > activity.LastHumanMessageAt;
        if (activity is not null && !newer)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        // 사람 메시지만 stage/claim을 초기화한다. 중복·과거 메시지는 cycle을 증가시키지 않는다.
        long cycle = activity is null ? 1 : unchecked(activity.RevivalCycleVersion + 1);
        DateTime next = ChatAiPolicies.ScheduleRevival(room.Id, cycle, 1,
            intent.CreatedAt.AddHours(settings.RevivalFirstDelayHours), settings.RevivalAllowedStartTime, settings.RevivalAllowedEndTime);
        if (activity is null)
        {
            activity = new ChatRoomAiActivityEntity
            {
                ChatRoomId = room.Id,
                CreatedAt = readClock(),
                CreatedBy = "SYSTEM"
            };
            db.ChatRoomAiActivities.Add(activity);
        }

        activity.LastHumanMessageId = intent.MessageId;
        activity.LastHumanMessageAt = intent.CreatedAt;
        activity.RevivalCycleVersion = cycle;
        activity.RevivalStage = 0;
        activity.LastRevivalAt = null;
        activity.NextRevivalAt = next;
        activity.RevivalStopped = false;
        ClearClaim(activity);
        Audit(activity);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<long>> FindDueAsync(DateTime now, int limit, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await (from activity in db.ChatRoomAiActivities.AsNoTracking()
                      join room in db.ChatRooms.AsNoTracking() on activity.ChatRoomId equals room.Id
                      where !activity.RevivalStopped && activity.NextRevivalAt != null && activity.NextRevivalAt <= now
                          && (activity.ClaimExpiresAt == null || activity.ClaimExpiresAt <= now)
                          && room.Active && room.DeletedAt == null && (room.RoomType == "GROUP" || room.RoomType == "OPEN")
                          && db.ChatRoomAiSettings.Any(setting => setting.ChatRoomId == room.Id && setting.RevivalEnabled)
                          && (from member in db.ChatRoomAiMembers
                              join agent in db.ChatAiAgents on member.AiAgentId equals agent.Id
                              where member.ChatRoomId == room.Id && member.Active && member.DeletedAt == null
                                  && agent.Active && agent.DeletedAt == null
                              select member.Id).Any()
                      orderby activity.NextRevivalAt, activity.Id
                      select activity.Id).Take(Math.Max(1, limit)).ToListAsync(cancellationToken);
    }

    public async Task<ChatAiRevivalClaim?> ClaimRevivalAsync(long activityId, DateTime now, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var roomId = await db.ChatRoomAiActivities.AsNoTracking().Where(row => row.Id == activityId)
            .Select(row => (long?)row.ChatRoomId).SingleOrDefaultAsync(cancellationToken);
        if (roomId is null)
        {
            return null;
        }

        // immutable room ID 탐색은 transaction 밖에서 마쳐, 잠금 이후의 설정 조회가 과거 snapshot에 묶이지 않게 한다.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var room = await LockRoomAsync(db, roomId.Value, cancellationToken);
        var rows = await db.ChatRoomAiActivities.FromSqlInterpolated(
            $"SELECT * FROM chat_room_ai_activity WHERE id = {activityId} FOR UPDATE").ToListAsync(cancellationToken);
        var activity = rows.SingleOrDefault();
        if (activity is null || activity.RevivalStopped || activity.NextRevivalAt is null || activity.NextRevivalAt > now
            || activity.ClaimExpiresAt > now)
        {
            return null;
        }

        // 만료 claim만 해제한다. 원본120초 값과 발화 스케줄을 임의로 늘리지 않는다.
        if (activity.ClaimToken is not null && (activity.ClaimExpiresAt is null || activity.ClaimExpiresAt <= now))
        {
            ClearClaim(activity);
        }

        ChatAiRevivalClaim? claim = null;
        bool eligible = room is { Active: true, DeletedAt: null } && room.RoomType is "GROUP" or "OPEN"
            && await db.ChatRoomAiSettings.AnyAsync(row => row.ChatRoomId == roomId && row.RevivalEnabled, cancellationToken);
        if (eligible)
        {
            var settings = await SettingsWithinAsync(db, cancellationToken);
            if (!ChatAiPolicies.WithinRevivalWindow(now, settings.RevivalAllowedStartTime, settings.RevivalAllowedEndTime))
            {
                activity.NextRevivalAt = ChatAiPolicies.ScheduleRevival(roomId.Value, activity.RevivalCycleVersion,
                    activity.RevivalStage + 1, now, settings.RevivalAllowedStartTime, settings.RevivalAllowedEndTime);
            }
            else
            {
                var candidates = await Candidates(db, roomId.Value).ToListAsync(cancellationToken);
                if (candidates.Count > 0)
                {
                    int previous = candidates.FindIndex(value => value.Member.Id == activity.LastRevivalAiMemberId);
                    var candidate = candidates[(previous + 1) % candidates.Count];
                    activity.ClaimToken = Guid.NewGuid().ToString("D");
                    activity.ClaimExpiresAt = now.AddSeconds(Math.Max(10, options.RevivalClaimTimeoutSeconds));
                    int stage = activity.RevivalStage + 1;
                    string requestId = $"chat-ai:revival:{activity.Id}:{activity.RevivalCycleVersion}:{stage}";
                    claim = new(activity.Id, roomId.Value, candidate.Member.Id, activity.ClaimToken,
                        activity.RevivalCycleVersion, stage, requestId);
                }
            }
        }

        Audit(activity);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claim;
    }

    public async Task FinishRevivalAsync(ChatAiRevivalClaim claim, ChatAiProcessingResult result,
        DateTime now, CancellationToken cancellationToken, TimeSpan? retryAfter = null)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockRoomAsync(db, claim.RoomId, cancellationToken);
        var rows = await db.ChatRoomAiActivities.FromSqlInterpolated(
            $"SELECT * FROM chat_room_ai_activity WHERE id = {claim.ActivityId} FOR UPDATE").ToListAsync(cancellationToken);
        var activity = rows.SingleOrDefault();
        if (activity is null || !Matches(activity, claim))
        {
            return;
        }

        var settings = await SettingsWithinAsync(db, cancellationToken);
        if (result == ChatAiProcessingResult.Failed)
        {
            activity.NextRevivalAt = ChatAiPolicies.ScheduleRevivalRetry(now, options.RevivalFailureRetryMinutes,
                settings.RevivalAllowedStartTime, settings.RevivalAllowedEndTime);

            // 기존 실패 예약이 Provider 대기 지시보다 이르면 다음 후보 시각만 뒤로 민다.
            if (retryAfter is { } wait)
            {
                DateTime providerNotBefore = readClock().Add(wait);
                if (activity.NextRevivalAt < providerNotBefore)
                {
                    activity.NextRevivalAt = providerNotBefore;
                }
            }
        }
        else
        {
            // SKIPPED와 DUPLICATE도 원본처럼 stage를 진행하며 세 번째 이후 멈춘다.
            activity.RevivalStage = claim.AttemptNumber;
            activity.LastRevivalAt = now;
            activity.LastRevivalAiMemberId = claim.AiMemberId;
            activity.RevivalStopped = claim.AttemptNumber >= 3;
            int delay = claim.AttemptNumber == 1 ? settings.RevivalSecondDelayHours : settings.RevivalThirdDelayHours;
            activity.NextRevivalAt = activity.RevivalStopped ? null : ChatAiPolicies.ScheduleRevival(
                claim.RoomId, claim.CycleVersion, claim.AttemptNumber + 1, now.AddHours(delay),
                settings.RevivalAllowedStartTime, settings.RevivalAllowedEndTime);
        }

        ClearClaim(activity);
        Audit(activity);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static bool Matches(ChatRoomAiActivityEntity activity, ChatAiRevivalClaim claim)
    {
        return activity.ClaimToken == claim.ClaimToken && activity.RevivalCycleVersion == claim.CycleVersion
                && activity.RevivalStage + 1 == claim.AttemptNumber;
    }

    private static void ClearClaim(ChatRoomAiActivityEntity activity)
    {
        activity.ClaimToken = null;
        activity.ClaimExpiresAt = null;
    }

    private void Audit(ChatRoomAiActivityEntity activity)
    {
        activity.UpdatedAt = readClock();
        activity.UpdatedBy = "SYSTEM";
    }

    private static async Task<ChatRoomEntity?> LockRoomAsync(ChatDbContext db, long roomId, CancellationToken cancellationToken)
    {
        // OPEN 폐쇄와 동일하게 OPEN → room 순서로 잠가 검사 뒤 폐쇄/저장 경합을 직렬화한다.
        var openRooms = await db.OpenChatRooms.FromSqlInterpolated(
            $"SELECT * FROM open_chat_room WHERE chat_room_id = {roomId} FOR UPDATE").ToListAsync(cancellationToken);
        var rooms = await db.ChatRooms.FromSqlInterpolated($"SELECT * FROM chat_room WHERE id = {roomId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return openRooms.Any(row => row.Status == "CLOSED") ? null : rooms.SingleOrDefault();
    }
}
