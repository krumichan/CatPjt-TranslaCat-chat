namespace TranslaCat.Chat.Infrastructure.Persistence.Entities;

// 알림 수신 정책만 저장한다. 읽음 cursor와 활동 알림 데이터는 변경하지 않는다.
public sealed class ChatNotificationPreferenceEntity
{
    public long UserId { get; set; }
    public bool PushEnabled { get; set; } = true;
    public DateTime? EnabledSinceUtc { get; set; }
}
