using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Application.Safety;
using TranslaCat.Chat.Infrastructure.Core;
using TranslaCat.Chat.Infrastructure.Persistence;

namespace TranslaCat.Chat.Infrastructure.Safety;

public sealed class ChatContentPolicy(IDbContextFactory<ChatDbContext> contexts, HttpChatCoreClient core) : IChatContentPolicy
{
    public async Task RequireActiveAsync(long userId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        if (userId <= 0 || await db.ChatAccountDeletions.AnyAsync(row => row.UserId == userId, token))
        {
            throw new ChatSafetyException("CHAT_ACCOUNT_DELETED", "탈퇴한 계정은 이용할 수 없습니다.");
        }
    }

    public async Task<IReadOnlyDictionary<long, ChatAuthorVisibility>> ResolveAsync(
        long userId, IReadOnlyList<long> senderIds, CancellationToken token)
    {
        await RequireActiveAsync(userId, token);
        long[] ids = senderIds.Distinct().ToArray();
        if (ids.Any(id => id <= 0)) throw new ChatCoreUnavailableException();

        await using var db = await contexts.CreateDbContextAsync(token);
        var deleted = await db.ChatAccountDeletions.AsNoTracking().Where(row => ids.Contains(row.UserId))
            .Select(row => row.UserId).ToListAsync(token);
        var results = ids.ToDictionary(id => id, id => new ChatAuthorVisibility(false, deleted.Contains(id)));

        // 탈퇴 신원 조회는 BE로 다시 보내지 않는다. 나머지는 100명 단위의 엄격한 양방향 관계 응답만 채택한다.
        foreach (long[] targets in ids.Where(id => id != userId && !deleted.Contains(id)).Chunk(100))
        {
            var relations = await core.RelationsAsync(userId, targets, token);
            if (relations.Relations is null || relations.MissingUserIds is not { Count: 0 }
                || relations.Relations.Count != targets.Length
                || relations.Relations.Any(row => row is null || !targets.Contains(row.TargetUserId))
                || relations.Relations.Select(row => row.TargetUserId).Distinct().Count() != targets.Length)
            {
                throw new ChatCoreUnavailableException();
            }

            foreach (var relation in relations.Relations)
                results[relation.TargetUserId] = new(relation.Blocked, false);
        }

        // 외부 관계 응답을 기다리는 동안 완료된 탈퇴를 예전 active snapshot으로 덮어쓰지 않는다.
        await RequireActiveAsync(userId, token);
        var deletedAfterLookup = await db.ChatAccountDeletions.AsNoTracking().Where(row => ids.Contains(row.UserId))
            .Select(row => row.UserId).ToListAsync(token);
        foreach (long id in deletedAfterLookup) results[id] = results[id] with { AuthorDeleted = true };

        return results;
    }

    public async Task<IReadOnlySet<long>> BlockedSendersAsync(long userId, CancellationToken token)
    {
        await RequireActiveAsync(userId, token);
        await using var db = await contexts.CreateDbContextAsync(token);
        var blocked = new HashSet<long>();
        long cursor = 0;
        while (true)
        {
            // badge/list는 현재 사용자에게 보일 수 있는 발신자만 100명씩 판정한다. 메시지 원문은 읽지 않는다.
            var candidates = await db.ChatMessages.AsNoTracking().Where(message => message.SenderType == "USER"
                    && message.MessageType != "SYSTEM" && message.SenderUserId != null && message.SenderUserId > cursor
                    && message.SenderUserId != userId && message.Status == "SENT" && message.DeletedAt == null
                    && db.ChatRoomMembers.Any(member => member.ChatRoomId == message.ChatRoomId && member.UserId == userId
                        && member.Active && member.DeletedAt == null && member.JoinedAt <= message.CreatedAt))
                .Select(message => message.SenderUserId!.Value).Distinct().Order().Take(100).ToArrayAsync(token);
            if (candidates.Length == 0) break;
            var policy = await ResolveAsync(userId, candidates, token);
            foreach (var entry in policy.Where(entry => entry.Value.Blocked)) blocked.Add(entry.Key);
            cursor = candidates[^1];
        }
        return blocked;
    }
}
