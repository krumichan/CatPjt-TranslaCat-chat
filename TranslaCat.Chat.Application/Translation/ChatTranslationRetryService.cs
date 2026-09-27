using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Application.Translation;

public sealed class ChatTranslationRetryService(
    IChatTranslationStore store, IChatTranslationDispatcher dispatcher, Func<DateTime> readClock)
{
    public async Task<ChatMessageTranslationView> RetryAsync(
        long userId, long roomId, long messageId, string? languageCode, CancellationToken cancellationToken = default)
    {
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
        var change = await store.RetryAsync(userId, roomId, messageId,
            ChatMessageText.Trim(languageCode).ToLowerInvariant(), dispatcher.IsConfigured, readClock(), cancellationToken);
        if (change.Intent is not null)
        {
            await dispatcher.DispatchAsync(change.Intent, cancellationToken);
        }

        return change.Translation;
    }
}
