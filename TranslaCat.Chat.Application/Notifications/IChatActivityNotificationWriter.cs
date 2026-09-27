namespace TranslaCat.Chat.Application.Notifications;

public sealed record ChatActivityNotificationRequest(
    long RecipientUserId,
    string RecipientEmail,
    string NotificationType,
    long? RoomId,
    long? ActorUserId,
    string PayloadJson,
    string SourceEventKey,
    string AuditIdentity);

public interface IChatActivityNotificationWriter
{
    // 초대/강퇴/역할/종료 source transaction의 commit 뒤 호출한다. 중복 source는 null을 반환한다.
    Task<ChatNotificationActivity?> CreateAsync(ChatActivityNotificationRequest request, CancellationToken cancellationToken);
}

public interface IChatActivityNotificationDelivery
{
    Task ValidateAvailabilityAsync(CancellationToken cancellationToken);
    Task DeliverAsync(string recipientEmail, ChatNotificationActivity notification, DateTime occurredAt, CancellationToken cancellationToken);
}
