using System.Text.Json;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Runtime;
using TranslaCat.Chat.Application.Translation;

namespace TranslaCat.Chat.Api.Translation;

public sealed class ChatTranslationRealtimeDelivery(
    ChatRealtimeRedisRelay relay,
    ChatTranslationContractMapper mapper,
    ChatReadLocalTime localTime,
    TimeProvider timeProvider) : IChatTranslationEventDelivery
{
    public Task DeliverAsync(ChatTranslationChanged change, CancellationToken cancellationToken)
    {
        // 실제 결과 commit 이후 호출되며 occurredAt은 전송 경계에서 생성한다.
        var payload = mapper.ToEvent(change, localTime.FromUtc(timeProvider.GetUtcNow()));
        return relay.PublishRoomAsync(change.ChatRoomId, JsonSerializer.Serialize(payload), cancellationToken);
    }
}
