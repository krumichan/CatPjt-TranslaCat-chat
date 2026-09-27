using System.Text.Json;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Translation;

namespace TranslaCat.Chat.Api.Runtime;

public sealed class ChatMessageRealtimeDelivery(
    IServiceProvider services,
    ChatMessageContractMapper mapper,
    ChatReadLocalTime localTime,
    TimeProvider timeProvider) : IChatMessageEventDelivery
{
    public Task ValidateAvailabilityAsync(IReadOnlyList<ChatMessageIntent> intents, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 필요한 실제 adapter가 빠진 상태에서 본문만 저장하고 성공하지 않는다.
        foreach (var intent in intents)
        {
            _ = intent switch
            {
                ChatMessageCreatedIntent => (object)Relay(),
                ChatTranslationRequestedIntent => Translation(),
                ChatHumanMessageRecordedIntent or ChatAiTriggerRequestedIntent => Ai(),
                _ => throw new InvalidOperationException("Unknown message intent.")
            };
        }
        return Task.CompletedTask;
    }

    public Task DeliverAsync(ChatMessageIntent intent, CancellationToken cancellationToken)
    {
        // EF transaction adapter가 실제 commit을 마친 뒤 호출한다. STOMP SEND도 재발행하지 않는다.
        return intent switch
        {
            ChatMessageCreatedIntent created => Relay().PublishRoomAsync(created.Message.ChatRoomId,
                JsonSerializer.Serialize(mapper.ToCreatedEvent(created.Message, localTime.FromUtc(timeProvider.GetUtcNow()))), cancellationToken),
            ChatTranslationRequestedIntent translation => Translation().DispatchAsync(translation, cancellationToken),
            ChatHumanMessageRecordedIntent human => Ai().RecordHumanAsync(human, cancellationToken),
            ChatAiTriggerRequestedIntent trigger => Ai().TriggerAsync(trigger, cancellationToken),
            _ => throw new InvalidOperationException("Unknown message intent.")
        };
    }

    private ChatRealtimeRedisRelay Relay()
    {
        return services.GetService<ChatRealtimeRedisRelay>()
        ?? throw new ChatMessageDependencyUnavailableException("realtime relay");
    }

    private IChatTranslationDispatcher Translation()
    {
        var dispatcher = services.GetService<IChatTranslationDispatcher>();
        return dispatcher?.IsConfigured == true ? dispatcher
            : throw new ChatMessageDependencyUnavailableException("translation dispatcher");
    }

    private IChatAiMessageDispatcher Ai()
    {
        var dispatcher = services.GetService<IChatAiMessageDispatcher>();
        return dispatcher?.IsConfigured == true ? dispatcher
            : throw new ChatMessageDependencyUnavailableException("AI dispatcher");
    }
}
