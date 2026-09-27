using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Ai;

public sealed partial class EfChatAiStore
{
    public async Task<ChatAiProcessingResult> SaveReplyAsync(ChatAiPlan plan, string reply,
        ChatAiRevivalClaim? revivalClaim, CancellationToken cancellationToken)
    {
        if (ChatMessageText.IsBlank(plan.Request.RequestId) || ChatMessageText.IsBlank(reply))
        {
            return ChatAiProcessingResult.Failed;
        }

        var intents = new List<ChatMessageIntent>();
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken))
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            long roomId = plan.Request.Room.RoomId;
            var room = await LockRoomAsync(db, roomId, cancellationToken);
            if (room is null || !room.Active || room.DeletedAt is not null)
            {
                return ChatAiProcessingResult.Failed;
            }

            if (await db.ChatMessages.AnyAsync(row => row.AiRequestId == plan.Request.RequestId, cancellationToken))
            {
                return ChatAiProcessingResult.Duplicate;
            }

            // 승인된 정합성 보완: 새 human/reset 이후 늦은 REVIVAL 결과를 같은 저장 transaction에서 거절한다.
            if (revivalClaim is not null)
            {
                var activities = await db.ChatRoomAiActivities.FromSqlInterpolated(
                    $"SELECT * FROM chat_room_ai_activity WHERE id = {revivalClaim.ActivityId} FOR UPDATE").ToListAsync(cancellationToken);
                if (activities.SingleOrDefault() is not { } activity || !Matches(activity, revivalClaim))
                {
                    return ChatAiProcessingResult.Failed;
                }
            }

            var candidate = await Candidates(db, roomId, plan.AiMemberId).SingleOrDefaultAsync(cancellationToken);
            if (candidate is null)
            {
                return ChatAiProcessingResult.Failed;
            }

            DateTime now = readClock();
            var message = new ChatMessageEntity
            {
                ChatRoomId = roomId,
                SenderAiMemberId = candidate.Member.Id,
                SenderType = "AI",
                MessageType = "TEXT",
                Content = ChatMessageText.Trim(reply),
                Status = "SENT",
                AiRequestId = plan.Request.RequestId,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = "SYSTEM",
                UpdatedBy = "SYSTEM"
            };
            db.ChatMessages.Add(message);
            await db.SaveChangesAsync(cancellationToken);

            // 사람 회원의 CHAT 언어 설정에 따라 원문 언어를 제외한 PENDING 번역을 함께 저장한다.
            var members = await db.ChatRoomMembers.AsNoTracking().Where(row => row.ChatRoomId == roomId
                && row.Active && row.DeletedAt == null).ToListAsync(cancellationToken);
            var users = members.Select(row => row.UserId).ToArray();
            var defaults = await db.UserChatLanguageSettings.AsNoTracking().Where(row => users.Contains(row.UserId))
                .ToDictionaryAsync(row => row.UserId, cancellationToken);
            var languages = members.Select(member =>
            {
                string fallback = defaults.GetValueOrDefault(member.UserId)?.TranslationLanguageCode ?? "ja";
                return ChatMessageText.IsBlank(member.TranslationLanguageCode)
                    ? fallback : ChatMessageText.Trim(member.TranslationLanguageCode!).ToLowerInvariant();
            }).Where(language => !ChatMessageText.IsBlank(language)
                && !language.Equals(candidate.Agent.OriginalLanguageCode, StringComparison.OrdinalIgnoreCase))
                .Select(language => ChatMessageText.Trim(language).ToLowerInvariant()).Distinct().ToArray();
            var translations = languages.Select(language => new ChatMessageTranslationEntity
            {
                ChatMessageId = message.Id,
                LanguageCode = language,
                Status = "PENDING",
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = "SYSTEM",
                UpdatedBy = "SYSTEM"
            }).ToArray();
            db.ChatMessageTranslations.AddRange(translations);
            await db.SaveChangesAsync(cancellationToken);

            string? image = null;
            if (!ChatMessageText.IsBlank(candidate.Agent.ProfileImageObjectKey))
            {
                var resolver = profiles ?? throw new ChatMessageDependencyUnavailableException("AI profile image storage");
                image = await resolver.ResolveObjectUrlAsync(candidate.Agent.ProfileImageObjectKey!, cancellationToken);
            }

            // unread는 실제 저장 정밀도의 created_at과 비교한다. 공개 응답 시각은 원본처럼 메모리 값을 유지한다.
            long unread = await (from member in db.ChatRoomMembers
                                 join stored in db.ChatMessages on member.ChatRoomId equals stored.ChatRoomId
                                 where stored.Id == message.Id && member.Active && member.DeletedAt == null
                                     && member.JoinedAt <= stored.CreatedAt
                                     && (member.LastReadMessageId == null || member.LastReadMessageId < stored.Id)
                                 select member.Id).LongCountAsync(cancellationToken);
            var view = new ChatMessageView(message.Id, roomId, null, candidate.Member.Id, candidate.Agent.Nickname, null,
                image, "AI", "TEXT", message.Content, "SENT", unread,
                Array.AsReadOnly(translations.Select(row => new ChatMessageTranslationView(row.Id, row.LanguageCode,
                    null, "PENDING", null, null)).ToArray()), now, now, null);
            intents.Add(new ChatMessageCreatedIntent(view));
            if (translations.Length > 0)
            {
                intents.Add(new ChatTranslationRequestedIntent(roomId, message.Id, null,
                    Array.AsReadOnly(translations.Select(row => row.Id).ToArray())));
            }

            await delivery.ValidateAvailabilityAsync(intents.AsReadOnly(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // 원본 AI message.created의 commit 전 전송도 사람 메시지와 같은 post-commit 경계로 개선한다.
        foreach (var intent in intents)
        {
            try
            {
                await delivery.DeliverAsync(intent, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning("AI message delivery failed. Event={EventType}, Failure={FailureType}",
                    intent.GetType().Name, exception.GetType().Name);
            }
        }

        return ChatAiProcessingResult.Responded;
    }
}
