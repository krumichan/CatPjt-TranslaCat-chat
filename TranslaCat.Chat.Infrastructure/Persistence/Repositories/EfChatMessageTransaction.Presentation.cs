using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Infrastructure.Persistence.Repositories;

public sealed partial class EfChatMessageTransaction
{
    private sealed partial class Session
    {
        private readonly Dictionary<long, ChatUserMessageProfile> userProfiles = [];

        public async Task<IReadOnlyList<ChatMessageView>> PresentAsync(
            ChatMessageMember member, IReadOnlyList<ChatStoredMessage> messages, CancellationToken cancellationToken)
        {
            if (messages.Count == 0)
            {
                return Array.Empty<ChatMessageView>();
            }

            // 반환할 window에 대해서만 번역과 미확인 인원을 읽는다.
            var ids = messages.Select(message => message.Id).ToArray();
            var translations = await context.ChatMessageTranslations.AsNoTracking()
                .Where(translation => ids.Contains(translation.ChatMessageId) && translation.DeletedAt == null)
                .ToListAsync(cancellationToken);
            var translationMap = translations.GroupBy(translation => translation.ChatMessageId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<ChatMessageTranslationView>)Array.AsReadOnly(
                    group.Select(translation => new ChatMessageTranslationView(translation.Id,
                        translation.LanguageCode, translation.TranslatedContent, translation.Status,
                        translation.FailureReason, translation.CompletedAt)).ToArray()));
            var unreadCounts = await (from message in context.ChatMessages.AsNoTracking()
                                      from reader in context.ChatRoomMembers.AsNoTracking()
                                      where ids.Contains(message.Id) && message.Status == "SENT"
                                          && message.DeletedAt == null && message.MessageType != "SYSTEM"
                                          && (message.SenderType == "USER" || message.SenderType == "AI")
                                          && reader.ChatRoomId == message.ChatRoomId && reader.Active
                                          && reader.DeletedAt == null && reader.JoinedAt <= message.CreatedAt
                                          && (message.SenderUserId == null || message.SenderUserId != reader.UserId)
                                          && (reader.LastReadMessageId == null || reader.LastReadMessageId < message.Id)
                                      group reader by message.Id into counts
                                      select new
                                      {
                                          MessageId = counts.Key,
                                          Count = counts.LongCount()
                                      })
                .ToDictionaryAsync(row => row.MessageId, row => row.Count, cancellationToken);

            var responses = new List<ChatMessageView>(messages.Count);
            foreach (var message in messages)
            {
                string? name = null;
                string? email = null;
                string? imageUrl = null;
                long? senderUserId = message.SenderUserId;
                OpenChatMessageSenderView? openSender = null;

                // OPEN 사람 발신자의 전역 신원은 응답에 포함하지 않는다. AI 표시는 원본 분기를 유지한다.
                if (message.SenderType == "AI")
                {
                    senderUserId = null;
                    if (message.SenderAiMemberId is not null)
                    {
                        var agent = await (from aiMember in context.ChatRoomAiMembers.AsNoTracking()
                                           join aiAgent in context.ChatAiAgents.AsNoTracking() on aiMember.AiAgentId equals aiAgent.Id
                                           where aiMember.Id == message.SenderAiMemberId
                                           select aiAgent).SingleOrDefaultAsync(cancellationToken);
                        name = agent?.Nickname;
                        imageUrl = await ResolveObjectUrlAsync(agent?.ProfileImageObjectKey, cancellationToken);
                    }
                }
                else if (member.RoomType == "OPEN")
                {
                    senderUserId = null;
                    if (message.MessageType != "SYSTEM" && message.SenderUserId is not null)
                    {
                        openSender = await ResolveOpenSenderAsync(member.ChatRoomId, message.SenderUserId.Value, cancellationToken);
                    }
                }
                else if (message.SenderUserId is not null)
                {
                    var profile = await ResolveUserAsync(message.SenderUserId.Value, cancellationToken);
                    name = profile.Name;
                    email = profile.Email;
                    imageUrl = profile.ProfileImageUrl;
                }

                responses.Add(new ChatMessageView(message.Id, message.ChatRoomId, senderUserId,
                    message.SenderAiMemberId, name, email, imageUrl, message.SenderType,
                    message.MessageType, message.Content, message.Status,
                    message.MessageType == "SYSTEM" ? null : unreadCounts.GetValueOrDefault(message.Id),
                    translationMap.GetValueOrDefault(message.Id) ?? Array.Empty<ChatMessageTranslationView>(),
                    message.CreatedAt, message.UpdatedAt, openSender));
            }

            return responses.AsReadOnly();
        }

        private async Task<ChatUserMessageProfile> ResolveUserAsync(long userId, CancellationToken cancellationToken)
        {
            if (userProfiles.TryGetValue(userId, out var cached))
            {
                return cached;
            }

            var profile = await profiles.GetUserAsync(userId, cancellationToken);
            if (profile is null || profile.UserId != userId)
            {
                throw new ChatMessageDependencyUnavailableException("verified user profile");
            }

            userProfiles.Add(userId, profile);
            return profile;
        }

        private async Task<OpenChatMessageSenderView?> ResolveOpenSenderAsync(
            long roomId, long userId, CancellationToken cancellationToken)
        {
            // 탈퇴/soft-delete된 발신자의 기존 OPEN 표시 정보도 원본처럼 보존한다.
            var sender = await (from profile in context.OpenChatMemberProfiles.AsNoTracking()
                                join member in context.ChatRoomMembers.AsNoTracking() on profile.ChatRoomMemberId equals member.Id
                                where member.ChatRoomId == roomId && member.UserId == userId
                                select new
                                {
                                    member.Id,
                                    profile.MemberCode,
                                    profile.Nickname,
                                    profile.ProfileImageObjectKey,
                                    member.Role
                                })
                .SingleOrDefaultAsync(cancellationToken);
            if (sender is null)
            {
                return null;
            }

            return new OpenChatMessageSenderView(sender.Id, sender.MemberCode, sender.Nickname,
                await ResolveObjectUrlAsync(sender.ProfileImageObjectKey, cancellationToken), sender.Role);
        }

        private Task<string?> ResolveObjectUrlAsync(string? objectKey, CancellationToken cancellationToken)
        {
            return ChatMessageText.IsBlank(objectKey)
                ? Task.FromResult<string?>(null)
                : profiles.ResolveObjectUrlAsync(objectKey!, cancellationToken);
        }
    }
}
