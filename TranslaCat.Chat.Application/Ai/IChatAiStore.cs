using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Application.Ai;

public interface IChatAiStore
{
    Task<IReadOnlyList<ChatAiPlan>> PlanAsync(long triggerMessageId, CancellationToken cancellationToken);
    Task<ChatAiPlan?> PlanRevivalAsync(ChatAiRevivalClaim claim, CancellationToken cancellationToken);
    Task<bool> ExistsReplyAsync(string requestId, CancellationToken cancellationToken);
    Task<ChatAiProcessingResult> SaveReplyAsync(ChatAiPlan plan, string reply,
        ChatAiRevivalClaim? revivalClaim, CancellationToken cancellationToken);
    Task<ChatAiSystemSettings> ReadSettingsAsync(CancellationToken cancellationToken);
    Task RecordHumanAsync(ChatHumanMessageRecordedIntent intent, CancellationToken cancellationToken);
    Task<IReadOnlyList<long>> FindDueAsync(DateTime now, int limit, CancellationToken cancellationToken);
    Task<ChatAiRevivalClaim?> ClaimRevivalAsync(long activityId, DateTime now, CancellationToken cancellationToken);
    Task FinishRevivalAsync(ChatAiRevivalClaim claim, ChatAiProcessingResult result,
        DateTime now, CancellationToken cancellationToken, TimeSpan? retryAfter = null);
}

public interface IChatAiDelay
{
    // AI 호출은 이미 끝난 상태다. callback은 별도 DI scope에서 실행할 수 있어야 한다.
    Task ScheduleAsync(ChatAiPlan plan, ChatAiReplyResponse response, TimeSpan delay, CancellationToken cancellationToken);
}
