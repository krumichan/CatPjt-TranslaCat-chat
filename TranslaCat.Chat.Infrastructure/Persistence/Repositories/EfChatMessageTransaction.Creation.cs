using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Repositories;

public sealed partial class EfChatMessageTransaction
{
    private sealed partial class Session
    {
        public async Task<ChatMessageLanguages> ResolveLanguagesAsync(
            ChatMessageMember member, CancellationToken cancellationToken)
        {
            var members = await context.ChatRoomMembers.AsNoTracking()
                .Where(candidate => candidate.ChatRoomId == member.ChatRoomId
                    && candidate.Active && candidate.DeletedAt == null)
                .ToListAsync(cancellationToken);
            var userIds = members.Select(candidate => candidate.UserId).ToArray();
            var defaults = await context.UserChatLanguageSettings.AsNoTracking()
                .Where(setting => userIds.Contains(setting.UserId))
                .ToDictionaryAsync(setting => setting.UserId, cancellationToken);

            // 사용자 CHAT 설정이 없으면 BE 원본의 ko/ja 기본값을 사용한다. BE 계정 DB는 조회하지 않는다.
            (string Original, string Translation) Resolve(ChatRoomMemberEntity candidate)
            {
                defaults.TryGetValue(candidate.UserId, out var userDefault);
                string original = userDefault?.OriginalLanguageCode ?? "ko";
                string translation = userDefault?.TranslationLanguageCode ?? "ja";
                if (candidate.OriginalLanguageCode is not null || candidate.TranslationLanguageCode is not null)
                {
                    original = NormalizeLanguage(candidate.OriginalLanguageCode, original);
                    translation = NormalizeLanguage(candidate.TranslationLanguageCode, translation);
                }

                return (original, translation);
            }

            var sender = members.Single(candidate => candidate.Id == member.Id);
            return new ChatMessageLanguages(Resolve(sender).Original,
                Array.AsReadOnly(members.Select(candidate => (string?)Resolve(candidate).Translation).ToArray()));
        }

        public async Task<ChatMessageCreation> InsertTextAsync(
            ChatMessageMember member, string content, IReadOnlyList<string> translationLanguages,
            DateTime createdAt, CancellationToken cancellationToken)
        {
            // 인증된 감사 주체를 명시적인 port로 받으며 현재 프로필 이메일로 추측하지 않는다.
            var auditor = await profiles.GetAuditIdentityAsync(member.UserId, cancellationToken);
            if (auditor is null || auditor.Length > 50)
            {
                throw new ChatMessageDependencyUnavailableException("bounded authenticated audit identity");
            }

            var message = new ChatMessageEntity
            {
                ChatRoomId = member.ChatRoomId,
                SenderUserId = member.UserId,
                SenderType = "USER",
                MessageType = "TEXT",
                Content = content,
                Status = "SENT",
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                CreatedBy = auditor,
                UpdatedBy = auditor
            };
            context.ChatMessages.Add(message);
            await context.SaveChangesAsync(cancellationToken);

            // pending 번역은 본문과 같은 transaction에 저장한다. 여기서는 Provider를 호출하지 않는다.
            var translations = translationLanguages.Select(language => new ChatMessageTranslationEntity
            {
                ChatMessageId = message.Id,
                LanguageCode = language,
                Status = "PENDING",
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                CreatedBy = auditor,
                UpdatedBy = auditor
            }).ToArray();
            context.ChatMessageTranslations.AddRange(translations);
            await context.SaveChangesAsync(cancellationToken);

            // 원본 managed entity처럼 요청 시각을 응답에 유지한다. 저장 후 재조회 값은 DB datetime(6) 정밀도다.
            var stored = new ChatStoredMessage(message.Id, message.ChatRoomId, message.SenderUserId,
                message.SenderAiMemberId, message.SenderType, message.MessageType, message.Content,
                message.Status, message.CreatedAt, message.UpdatedAt);
            return new ChatMessageCreation(stored, Array.AsReadOnly(translations.Select(translation => translation.Id).ToArray()));
        }

        private static string NormalizeLanguage(string? value, string fallback)
        {
            return ChatMessageText.IsBlank(value) ? fallback : ChatMessageText.Trim(value!).ToLowerInvariant();
        }
    }
}
