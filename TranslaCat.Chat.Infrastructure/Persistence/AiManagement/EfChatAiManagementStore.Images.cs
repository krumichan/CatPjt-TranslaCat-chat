using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.OpenMembership;
using TranslaCat.Chat.Application.ProfileImages;
using TranslaCat.Chat.Infrastructure.Persistence.ProfileImages;

namespace TranslaCat.Chat.Infrastructure.Persistence.AiManagement;

public sealed partial class EfChatAiManagementStore : IChatAiImageStore
{
    public Task<ChatAiMember> UploadImageAsync(long userId, long roomId, long memberId, ChatProfileImageKind kind,
        ChatProfileImageUpload upload, DateTime now, CancellationToken cancellationToken)
    {
        return ChangeImageAsync(userId, roomId, memberId, kind, upload, false, now, cancellationToken);
    }

    public Task<ChatAiMember> DeleteImageAsync(long userId, long roomId, long memberId, ChatProfileImageKind kind,
        DateTime now, CancellationToken cancellationToken)
    {
        return ChangeImageAsync(userId, roomId, memberId, kind, null, true, now, cancellationToken);
    }

    private async Task<ChatAiMember> ChangeImageAsync(long userId, long roomId, long memberId, ChatProfileImageKind kind,
        ChatProfileImageUpload? upload, bool delete, DateTime now, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        IChatProfileImageObjectStore? objects = null;
        string? newKey = null;
        string? oldKey = null;
        bool commitAttempted = false;
        ChatAiMember result;
        var intent = new OpenMembersChanged(roomId, now);
        try
        {
            // 관리 API와 같은 방/멤버 lock 및 OWNER/ADMIN·OPEN ban/종료 경계를 재사용한다.
            await AccessAsync(db, userId, roomId, manage: true, write: true, token);
            var member = await MemberAsync(db, roomId, memberId, token);
            var agent = await db.ChatAiAgents.SingleAsync(row => row.Id == member.AiAgentId, token);
            var validated = delete ? null : ChatProfileImagePolicy.Validate(upload, kind);
            oldKey = kind == ChatProfileImageKind.Profile ? agent.ProfileImageObjectKey : agent.ProfileBackgroundImageObjectKey;
            if (!delete || !ChatProfileImagePolicy.IsJavaBlank(oldKey))
            {
                objects = storage as IChatProfileImageObjectStore ?? throw new ChatProfileImageUnavailableException();
            }
            await events.ValidateAvailabilityAsync([intent], token);
            var audit = await AuditAsync(userId, token);

            if (validated is not null)
            {
                newKey = ChatProfileImagePolicy.ObjectKey(memberId, false, kind, validated.Extension);
                await objects!.StoreAsync(newKey, validated.ContentType, validated.Bytes, token);
            }
            if (kind == ChatProfileImageKind.Profile)
            {
                agent.ProfileImageObjectKey = newKey;
            }
            else
            {
                agent.ProfileBackgroundImageObjectKey = newKey;
            }
            agent.UpdatedAt = now;
            agent.UpdatedBy = audit;
            await db.SaveChangesAsync(token);

            // URL projection도 commit 전이다. 실패하면 key 변경 rollback과 신규 object cleanup을 수행한다.
            result = await MapAsync(db, member, token);
            commitAttempted = true;
            await transaction.CommitAsync(token);
        }
        catch
        {
            await ChatProfileImageCleanup.RollbackAsync(transaction, commitAttempted, objects, newKey, logger);
            throw;
        }

        if (objects is not null)
        {
            await ChatProfileImageCleanup.DeleteAsync(objects, oldKey, logger);
        }
        try
        {
            await events.DeliverAsync(intent, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning("AI image post-commit event failed ({FailureType}).", exception.GetType().Name);
        }
        return result;
    }
}
