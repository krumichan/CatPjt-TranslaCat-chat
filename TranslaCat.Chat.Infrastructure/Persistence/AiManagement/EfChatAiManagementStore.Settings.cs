using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.OpenMembership;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.AiManagement;

public sealed partial class EfChatAiManagementStore
{
    public Task<ChatAiRoomSettings> RoomSettingsAsync(long userId, long roomId, ChatAiRoomPatch? patch, DateTime now, CancellationToken token)
    {
        return ExecuteAsync(async (db, intents) =>
        {
            // 조회도 원본처럼 기본 설정을 저장한다. 동시 최초 생성은 동일 방 잠금으로 직렬화한다.
            await AccessAsync(db, userId, roomId, manage: patch is not null, write: true, token);
            var audit = await AuditAsync(userId, token);
            var setting = await RoomSettingAsync(db, roomId, now, audit, token);
            var previous = setting.DisclosureType;
            if (patch is not null)
            {
                if (patch.DisclosureType is not (null or "PUBLIC" or "PRIVATE") || patch.MentionPermission is not (null or "ALL_MEMBERS" or "OWNER_ADMIN_ONLY"))
                {
                    throw Error("채팅방 AI 설정 요청이 올바르지 않습니다.", "SETTING_INVALID");
                }

                setting.DisclosureType = patch.DisclosureType ?? setting.DisclosureType;
                setting.MentionPermission = patch.MentionPermission ?? setting.MentionPermission;
                setting.ConversationEnabled = patch.ConversationEnabled ?? setting.ConversationEnabled;
                setting.RevivalEnabled = patch.RevivalEnabled ?? setting.RevivalEnabled;
                setting.UpdatedAt = now;
                setting.UpdatedBy = audit;
            }
            await db.SaveChangesAsync(token);
            if (previous != setting.DisclosureType)
            {
                intents.Add(new OpenMembersChanged(roomId, now));
            }

            var system = await EfChatAiSystemSettings.GetOrCreateWithinAsync(db, now, audit, token);
            int count = await db.ChatRoomAiMembers.CountAsync(row => row.ChatRoomId == roomId && row.Active && row.DeletedAt == null, token);
            return new ChatAiRoomSettings(roomId, count > 0, count, system.MaxAiMembersPerRoom,
                setting.DisclosureType, setting.MentionPermission, setting.ConversationEnabled, setting.RevivalEnabled);
        }, token);
    }

    public Task<ChatAiSystemSettings> SystemSettingsAsync(long userId, ChatAiSystemPatch? patch, DateTime now, CancellationToken token)
    {
        return ExecuteAsync(async (db, intents) =>
        {
            var audit = await AuditAsync(userId, token);
            var entity = await EfChatAiSystemSettings.GetOrCreateWithinAsync(db, now, audit, token, forUpdate: patch is not null);
            var value = EfChatAiSystemSettings.Map(entity);
            if (patch is not null)
            {
                value = ChatAiSystemPolicy.Merge(value, patch);
                EfChatAiSystemSettings.Apply(entity, value);
                entity.UpdatedAt = now;
                entity.UpdatedBy = audit;
                await db.SaveChangesAsync(token);
            }
            return value;
        }, token);
    }

    private static async Task<ChatRoomAiSettingEntity> RoomSettingAsync(ChatDbContext db, long roomId, DateTime now, string audit, CancellationToken token)
    {
        var setting = await db.ChatRoomAiSettings.SingleOrDefaultAsync(row => row.ChatRoomId == roomId, token);
        if (setting is not null)
        {
            return setting;
        }

        setting = new ChatRoomAiSettingEntity
        {
            ChatRoomId = roomId,
            DisclosureType = "PUBLIC",
            MentionPermission = "ALL_MEMBERS",
            ConversationEnabled = true,
            RevivalEnabled = true,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = audit,
            UpdatedBy = audit
        };
        db.ChatRoomAiSettings.Add(setting);
        await db.SaveChangesAsync(token);
        return setting;
    }
}
