using TranslaCat.Chat.Application.Realtime;

namespace TranslaCat.Chat.Application.Messaging;

public sealed partial class ChatMessageService(
    IChatMessageTransaction transaction,
    Func<DateTime> readClock) : IChatRealtimeMessageSender
{
    public async Task SendTextAsync(long userId, long roomId, string content, CancellationToken cancellationToken)
    {
        await CreateTextAsync(userId, roomId, content, cancellationToken);
    }

    public Task<ChatMessageView> CreateTextAsync(
        long userId, long roomId, string? content, CancellationToken cancellationToken = default)
    {
        // Java 서비스의 trim/UTF-16 길이 규칙이다. HTTP의 raw @Size 검증과 분리한다.
        string normalized = ChatMessageText.Trim(content ?? string.Empty);
        if (normalized.Length == 0)
        {
            throw new ChatMessageException("메시지 내용은 필수입니다.");
        }

        if (normalized.Length > 5000)
        {
            throw new ChatMessageException("메시지는 5000자 이하로 입력해주세요.");
        }

        return transaction.ExecuteAsync(async (session, token) =>
        {
            // 동일 service를 REST와 STOMP에서 사용하며 저장 전에 접근과 OPEN 종료 상태를 확인한다.
            var member = await session.GetMemberAsync(userId, roomId, true, token);
            var languages = await session.ResolveLanguagesAsync(member, token);
            var targets = SelectTranslationLanguages(languages);
            var createdAt = readClock();
            var creation = await session.InsertTextAsync(member, normalized, targets, createdAt, token);
            var presented = (await session.PresentAsync(member, [creation.Message], token)).Single();
            var response = presented with
            {
                Translations = Array.AsReadOnly(presented.Translations.ToArray())
            };

            // 의도 등록만 수행한다. 외부 전달은 transaction port가 commit 성공 뒤에 실행한다.
            session.RegisterAfterCommit(new ChatMessageCreatedIntent(response));
            if (creation.TranslationIds.Count > 0)
            {
                session.RegisterAfterCommit(new ChatTranslationRequestedIntent(
                    roomId, response.Id, userId,
                    Array.AsReadOnly(creation.TranslationIds.ToArray())));
            }

            if (member.RoomType is "GROUP" or "OPEN")
            {
                session.RegisterAfterCommit(new ChatHumanMessageRecordedIntent(response.Id, roomId, response.CreatedAt));
                session.RegisterAfterCommit(new ChatAiTriggerRequestedIntent(response.Id));
            }

            return response;
        }, cancellationToken);
    }

    private static IReadOnlyList<string> SelectTranslationLanguages(ChatMessageLanguages languages)
    {
        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 활성 사람 멤버의 번역 언어만 사용한다. 표시 설정이 꺼져 있어도 원본처럼 후보에 포함한다.
        foreach (var target in languages.MemberTranslationLanguageCodes)
        {
            if (target is null || ChatMessageText.Trim(target).Length == 0
                || string.Equals(target, languages.SenderOriginalLanguageCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalized = ChatMessageText.Trim(target).ToLowerInvariant();
            if (seen.Add(normalized))
            {
                targets.Add(normalized);
            }
        }

        return targets.AsReadOnly();
    }
}
