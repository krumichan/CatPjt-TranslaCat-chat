using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Application.Translation;

public sealed class ChatTranslationRetryService(
    IChatTranslationStore store, IChatTranslationDispatcher dispatcher, Func<DateTime> readClock)
{
    /// <summary>지정한 언어의 메시지 번역 재시도를 요청하고 저장된 번역 상태를 반환한다.</summary>
    public async Task<ChatMessageTranslationView> RetryAsync(
        long userId,
        long roomId,
        long messageId,
        string? languageCode,
        CancellationToken cancellationToken = default)
    {
        // 저장소에 접근하기 전에 채팅방·메시지 ID와 대상 언어 입력을 확인한다.
        if (roomId <= 0)
        {
            throw new ChatMessageException("chatRoomId는 1 이상이어야 합니다.");
        }

        if (messageId <= 0)
        {
            throw new ChatMessageException("messageId는 1 이상이어야 합니다.");
        }

        if (languageCode is null || ChatMessageText.Trim(languageCode).Length == 0)
        {
            throw new ChatMessageException("languageCode는 필수입니다.");
        }

        // 접근/대상 검증과 no-op 판정 뒤, 실제 FAILED 변경 전에 외부 구성 여부를 확인한다.
        var change = await store.RetryAsync(
            userId,
            roomId,
            messageId,
            ChatMessageText.Trim(languageCode).ToLowerInvariant(),
            dispatcher.IsConfigured,
            readClock(),
            cancellationToken);

        // 저장소가 재시도 작업을 만든 경우에만 외부 번역 실행을 요청한다.
        if (change.Intent is not null)
        {
            await dispatcher.DispatchAsync(change.Intent, cancellationToken);
        }

        return change.Translation;
    }
}
