using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Application.Translation;

public interface IChatTranslationDispatcher
{
    // 설정 확인만 수행한다. 연결 확인을 명목으로 실제 AI Provider를 호출하지 않는다.
    bool IsConfigured
    {
        get;
    }

    Task DispatchAsync(ChatTranslationRequestedIntent intent, CancellationToken cancellationToken);
}
