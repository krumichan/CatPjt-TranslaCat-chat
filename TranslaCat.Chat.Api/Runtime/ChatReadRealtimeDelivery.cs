using System.Text.Json;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.Read;

namespace TranslaCat.Chat.Api.Runtime;

public sealed class ChatReadRealtimeDelivery(
    ChatRealtimeRedisRelay relay,
    ChatReadContractMapper mapper,
    ChatReadLocalTime localTime,
    TimeProvider timeProvider) : IChatReadEventDelivery
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task DeliverAsync(ChatReadUpdated message, CancellationToken cancellationToken)
    {
        // 원본처럼 recipient가 없으면 자기 큐 전달만 생략한다. 시각은 commit 이후 이 지점에서 생성한다.
        if (string.IsNullOrWhiteSpace(message.DestinationUsername))
        {
            return Task.CompletedTask;
        }

        var payload = mapper.ToSelfEvent(message, localTime.FromUtc(timeProvider.GetUtcNow()));
        return relay.PublishUserAsync(message.DestinationUsername, "/queue/chat/read", JsonSerializer.Serialize(payload, Json), cancellationToken);
    }

    public Task DeliverAsync(ChatMemberReadUpdated message, CancellationToken cancellationToken)
    {
        var payload = mapper.ToMemberEvent(message, localTime.FromUtc(timeProvider.GetUtcNow()));
        return relay.PublishRoomAsync(message.ChatRoomId, JsonSerializer.Serialize(payload, Json), cancellationToken);
    }
}
