using System.Text.Json;
using TranslaCat.Chat.Api.Runtime;
using TranslaCat.Chat.Application.Notifications;

namespace TranslaCat.Chat.Api.Notifications;

public sealed class ChatActivityNotificationRealtimeDelivery(IServiceProvider services, ChatNotificationContractMapper mapper)
    : IChatActivityNotificationDelivery
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task ValidateAvailabilityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = Relay();
        return Task.CompletedTask;
    }

    public Task DeliverAsync(string recipientEmail, ChatNotificationActivity notification, DateTime occurredAt, CancellationToken cancellationToken)
    {
        // 알림 저장 transaction의 commit 뒤 검증된 계정 user queue로만 전송한다.
        return Relay().PublishUserAsync(recipientEmail, "/queue/chat/notifications",
            JsonSerializer.Serialize(mapper.ToCreatedEvent(notification, occurredAt), Json), cancellationToken);
    }

    private ChatRealtimeRedisRelay Relay()
    {
        return services.GetService<ChatRealtimeRedisRelay>()
        ?? throw new ChatNotificationDependencyUnavailableException();
    }
}
