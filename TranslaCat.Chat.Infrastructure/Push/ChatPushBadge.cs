using TranslaCat.Chat.Application.Notifications;

namespace TranslaCat.Chat.Infrastructure.Push;

public static class ChatPushBadge
{
    public static int Count(ChatNotificationSummary summary)
    {
        // 기존 TotalAttentionCount와 같은 메시지+활동 합계다. 방 수·OPEN 초대를 다시 더하지 않는다.
        // 비정상 음수와 long 합산 overflow도 Expo의 안전한 정수 범위로 제한한다.
        decimal total = (decimal)summary.UnreadChatMessageCount + summary.UnreadActivityCount;
        return (int)Math.Clamp(total, 0, int.MaxValue);
    }

    public static async Task<int?> ReadAsync(IChatNotificationStore? notifications, long userId, CancellationToken token)
    {
        if (notifications is null) return null;
        try
        {
            return Count(await notifications.GetSummaryAsync(userId, token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // 요약 실패를 읽음 0으로 위장하지 않는다. badge만 생략하고 메시지 전달 정책은 다시 확인한다.
            return null;
        }
    }
}
