using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.AiManagement;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Ai;

public sealed partial class EfChatAiStore(
    IDbContextFactory<ChatDbContext> contexts,
    ChatAiOptions options,
    IChatMessageEventDelivery delivery,
    Func<DateTime> readClock,
    Func<int, int> nextRandom,
    ILogger<EfChatAiStore> logger,
    IChatAiUserNameReader? names = null,
    IChatMessageProfileReader? profiles = null) : IChatAiStore
{
    public async Task<IReadOnlyList<ChatAiPlan>> PlanAsync(long triggerMessageId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var trigger = await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(row => row.Id == triggerMessageId
            && row.DeletedAt == null, cancellationToken);
        if (trigger is null || trigger.Status != "SENT" || trigger.SenderType != "USER" || trigger.SenderUserId is null)
        {
            return [];
        }

        // 잠금 전 조회로 REPEATABLE READ snapshot을 고정하지 않는다. 잠금 대기 중 변경된 권한/설정을 읽는다.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var room = await LockRoomAsync(db, trigger.ChatRoomId, cancellationToken);
        if (room is null || !room.Active || room.DeletedAt is not null || room.RoomType is not ("GROUP" or "OPEN"))
        {
            return [];
        }

        var candidates = await Candidates(db, room.Id).ToListAsync(cancellationToken);
        var setting = await db.ChatRoomAiSettings.AsNoTracking().SingleOrDefaultAsync(row => row.ChatRoomId == room.Id, cancellationToken);
        if (candidates.Count == 0 || setting is null)
        {
            return [];
        }

        var system = await SettingsWithinAsync(db, cancellationToken);
        var sender = await db.ChatRoomMembers.AsNoTracking().SingleOrDefaultAsync(row => row.ChatRoomId == room.Id
            && row.UserId == trigger.SenderUserId && row.Active && row.DeletedAt == null, cancellationToken);
        if (sender is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }

        // 명시 멘션이 있으면 권한/상한 실패 시에도 CONVERSATION으로 우회하지 않는다.
        string triggerType;
        var mentioned = candidates.Where(candidate => ChatAiPolicies.ContainsMention(trigger.Content, candidate.Agent.Nickname)).ToArray();
        IReadOnlyList<Candidate> selected;
        if (mentioned.Length > 0)
        {
            if (setting.MentionPermission != "ALL_MEMBERS" && sender.Role is not ("OWNER" or "ADMIN"))
            {
                await transaction.CommitAsync(cancellationToken);
                return [];
            }

            var start = trigger.CreatedAt.AddSeconds(-system.MentionRateLimitWindowSeconds);
            var prior = await db.ChatMessages.AsNoTracking().Where(row => row.ChatRoomId == room.Id
                && row.SenderUserId == trigger.SenderUserId && row.SenderType == "USER" && row.Status == "SENT"
                && row.DeletedAt == null && row.CreatedAt >= start && row.Id < trigger.Id)
                .Select(row => row.Content).ToListAsync(cancellationToken);
            int priorCalls = prior.Sum(content => candidates.Count(candidate => ChatAiPolicies.ContainsMention(content, candidate.Agent.Nickname)));
            selected = mentioned.Take(Math.Max(0, system.MentionRateLimitCount - priorCalls)).ToArray();
            triggerType = "MENTION";
        }
        else
        {
            if (!setting.ConversationEnabled)
            {
                await transaction.CommitAsync(cancellationToken);
                return [];
            }

            var lastAi = await db.ChatMessages.AsNoTracking().Where(row => row.ChatRoomId == room.Id
                && row.SenderType == "AI" && row.Status == "SENT" && row.DeletedAt == null && row.Id < trigger.Id)
                .OrderByDescending(row => row.Id).FirstOrDefaultAsync(cancellationToken);
            if (lastAi is not null && system.ConversationCooldownSeconds > 0
                && trigger.CreatedAt < lastAi.CreatedAt.AddSeconds(system.ConversationCooldownSeconds))
            {
                await transaction.CommitAsync(cancellationToken);
                return [];
            }

            long lastAiId = lastAi?.Id ?? 0;
            long humans = await db.ChatMessages.LongCountAsync(row => row.ChatRoomId == room.Id
                && row.SenderType == "USER" && row.Status == "SENT" && row.DeletedAt == null
                && row.Id > lastAiId && row.Id <= trigger.Id, cancellationToken);
            if (humans < system.ConversationMinHumanMessagesAfterAi || system.ConversationResponseRate <= 0
                || (system.ConversationResponseRate < 100 && nextRandom(100) >= system.ConversationResponseRate))
            {
                await transaction.CommitAsync(cancellationToken);
                return [];
            }

            selected = [candidates[ChatAiPolicies.ConversationCandidateIndex(trigger.Id, candidates.Count)]];
            triggerType = "CONVERSATION";
        }

        var plans = new List<ChatAiPlan>();
        foreach (var candidate in selected)
        {
            string requestId = $"chat-ai:{triggerType.ToLowerInvariant()}:{trigger.Id}:{candidate.Member.Id}";
            plans.Add(await BuildPlanAsync(db, room, candidate, system, triggerType, requestId, trigger, cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return plans.AsReadOnly();
    }

    public async Task<ChatAiPlan?> PlanRevivalAsync(ChatAiRevivalClaim claim, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var room = await LockRoomAsync(db, claim.RoomId, cancellationToken);
        if (room is null || !room.Active || room.DeletedAt is not null || room.RoomType is not ("GROUP" or "OPEN"))
        {
            return null;
        }

        var candidate = await Candidates(db, room.Id, claim.AiMemberId).SingleOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        var settings = await SettingsWithinAsync(db, cancellationToken);
        var plan = await BuildPlanAsync(db, room, candidate, settings, "REVIVAL", claim.RequestId, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return plan;
    }

    public async Task<ChatAiSystemSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var settings = await SettingsWithinAsync(db, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return settings;
    }

    public async Task<bool> ExistsReplyAsync(string requestId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.ChatMessages.AnyAsync(row => row.AiRequestId == requestId, cancellationToken);
    }

    private async Task<ChatAiPlan> BuildPlanAsync(ChatDbContext db, ChatRoomEntity room, Candidate candidate,
        ChatAiSystemSettings settings, string triggerType, string requestId, ChatMessageEntity? trigger,
        CancellationToken cancellationToken)
    {
        int maxMessages = Math.Min(settings.ContextMaxMessages, 100);
        int maxCharacters = Math.Min(settings.ContextMaxCharacters, 50000);
        int replyCharacters = Math.Min(settings.ReplyMaxCharacters, 4000);
        var query = db.ChatMessages.AsNoTracking().Where(row => row.ChatRoomId == room.Id && row.Status == "SENT" && row.DeletedAt == null);
        if (trigger is not null)
        {
            query = query.Where(row => row.Id < trigger.Id);
        }

        var context = await query.OrderByDescending(row => row.Id).Take(maxMessages).ToListAsync(cancellationToken);
        context.Reverse();
        int characters = context.Sum(row => row.Content.Length);
        while (characters > maxCharacters && context.Count > 0)
        {
            characters -= context[0].Content.Length;
            context.RemoveAt(0);
        }

        // 원본은 실제 user ID 대신 문맥 등장 순서의 member-N 별칭을 보낸다.
        var users = context.Where(row => row.SenderUserId is not null).Select(row => row.SenderUserId!.Value).ToList();
        if (trigger?.SenderUserId is { } senderId)
        {
            users.Add(senderId);
        }
        var uniqueUsers = users.Distinct().ToArray();
        var aliases = uniqueUsers.Select((id, index) => (id, alias: "member-" + (index + 1))).ToDictionary(value => value.id, value => value.alias);
        var resolvedNames = new Dictionary<long, string>();
        var openNames = room.RoomType == "OPEN"
            ? await (from member in db.ChatRoomMembers.AsNoTracking()
                     join profile in db.OpenChatMemberProfiles.AsNoTracking() on member.Id equals profile.ChatRoomMemberId
                     where member.ChatRoomId == room.Id && uniqueUsers.Contains(member.UserId)
                     select new
                     {
                         member.UserId,
                         profile.Nickname
                     }).ToDictionaryAsync(value => value.UserId, value => value.Nickname, cancellationToken)
            : [];
        foreach (long userId in uniqueUsers)
        {
            string? nickname = openNames.GetValueOrDefault(userId);
            if (ChatMessageText.IsBlank(nickname))
            {
                var resolver = names ?? throw new ChatMessageDependencyUnavailableException("AI context user name");
                nickname = await resolver.GetUserNameAsync(userId, cancellationToken);
            }

            resolvedNames[userId] = Truncate(ChatMessageText.IsBlank(nickname) ? "Member" : nickname!, 100);
        }

        var aiIds = context.Where(row => row.SenderAiMemberId is not null).Select(row => row.SenderAiMemberId!.Value).Distinct().ToArray();
        var aiNames = await (from member in db.ChatRoomAiMembers.AsNoTracking()
                             join agent in db.ChatAiAgents.AsNoTracking() on member.AiAgentId equals agent.Id
                             where aiIds.Contains(member.Id)
                             select new
                             {
                                 member.Id,
                                 agent.Nickname
                             }).ToDictionaryAsync(value => value.Id, value => value.Nickname, cancellationToken);
        var messages = context.Select(message =>
        {
            string? id = null;
            string? name = null;
            if (message.SenderType == "USER" && message.SenderUserId is { } user)
            {
                id = aliases[user];
                name = resolvedNames[user];
            }
            else if (message.SenderType == "AI" && message.SenderAiMemberId is { } ai && aiNames.TryGetValue(ai, out var aiName))
            {
                id = "ai-" + ai;
                name = aiName;
            }

            return new ChatAiContextMessage(message.Id, message.SenderType, id, name, Truncate(message.Content, 5000), message.CreatedAt);
        }).ToArray();
        ChatAiTriggerMessage? triggerDto = trigger is null ? null : new(trigger.Id, aliases[trigger.SenderUserId!.Value],
            resolvedNames[trigger.SenderUserId.Value], trigger.Content, trigger.CreatedAt);
        var selectedAgent = candidate.Agent;
        return new(candidate.Member.Id, new(requestId, triggerType, new(room.Id, room.RoomType, room.Name, room.Description),
            new(candidate.Member.Id, selectedAgent.Nickname, selectedAgent.Bio, selectedAgent.PersonaPrompt, selectedAgent.OriginalLanguageCode),
            triggerDto, Array.AsReadOnly(messages), maxMessages, maxCharacters, replyCharacters));
    }

    private async Task<ChatAiSystemSettings> SettingsWithinAsync(ChatDbContext db, CancellationToken cancellationToken)
    {
        return EfChatAiSystemSettings.Map(await EfChatAiSystemSettings.GetOrCreateWithinAsync(db, readClock(), "SYSTEM", cancellationToken));
    }

    private static IQueryable<Candidate> Candidates(ChatDbContext db, long roomId, long? memberId = null)
    {
        return from member in db.ChatRoomAiMembers.AsNoTracking()
               join agent in db.ChatAiAgents.AsNoTracking() on member.AiAgentId equals agent.Id
               where member.ChatRoomId == roomId && (memberId == null || member.Id == memberId)
                   && member.Active && member.DeletedAt == null && agent.Active && agent.DeletedAt == null
               orderby member.JoinedAt
               select new Candidate(member, agent);
    }

    private static string Truncate(string value, int limit)
    {
        return value.Length <= limit ? value : value[..limit];
    }

    private sealed record Candidate(ChatRoomAiMemberEntity Member, ChatAiAgentEntity Agent);
}
