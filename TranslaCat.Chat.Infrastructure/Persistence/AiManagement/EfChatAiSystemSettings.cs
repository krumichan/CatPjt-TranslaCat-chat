using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.AiManagement;

public static class EfChatAiSystemSettings
{
    public static async Task<ChatAiSystemSettingEntity> GetOrCreateWithinAsync(
        ChatDbContext db, DateTime now, string? audit, CancellationToken token, bool forUpdate = false)
    {
        // 정상 조회는 전역 행을 잠그지 않는다. 다른 방의 느린 계정 조회까지 직렬화하지 않는다.
        if (!forUpdate)
        {
            var existing = await db.ChatAiSystemSettings.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == "DEFAULT", token);
            if (existing is not null
                && (existing.ResponseDelayMinMillis != 0 || existing.ResponseDelayMaxMillis != 0))
            {
                return existing;
            }
        }

        // 최초 생성·legacy 보정·관리자 PATCH만 현재 행을 잠근다.
        // 중복 시에도 처음부터 X lock을 얻어 INSERT IGNORE의 S→X 승격 경합을 피한다.
        var value = ChatAiSystemPolicy.Defaults;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO chat_ai_system_setting
            (id, max_ai_members_per_room, conversation_response_rate, conversation_cooldown_seconds, conversation_min_human_messages_after_ai, response_delay_enabled, response_delay_min_millis, response_delay_max_millis, revival_first_delay_hours, revival_second_delay_hours, revival_third_delay_hours, revival_allowed_start_time, revival_allowed_end_time, context_max_messages, context_max_characters, reply_max_characters, mention_rate_limit_count, mention_rate_limit_window_seconds, created_at, updated_at, created_by, updated_by)
            VALUES ('DEFAULT', {value.MaxAiMembersPerRoom}, {value.ConversationResponseRate}, {value.ConversationCooldownSeconds}, {value.ConversationMinHumanMessagesAfterAi}, {value.ResponseDelayEnabled}, {value.ResponseDelayMinMillis}, {value.ResponseDelayMaxMillis}, {value.RevivalFirstDelayHours}, {value.RevivalSecondDelayHours}, {value.RevivalThirdDelayHours}, {value.RevivalAllowedStartTime}, {value.RevivalAllowedEndTime}, {value.ContextMaxMessages}, {value.ContextMaxCharacters}, {value.ReplyMaxCharacters}, {value.MentionRateLimitCount}, {value.MentionRateLimitWindowSeconds}, {now}, {now}, {audit}, {audit})
            ON DUPLICATE KEY UPDATE id = 'DEFAULT'
            """, token);
        var rows = await db.ChatAiSystemSettings.FromSqlRaw(
            "SELECT * FROM chat_ai_system_setting WHERE id = 'DEFAULT' FOR UPDATE").AsNoTracking().ToListAsync(token);
        var entity = rows.Single();
        db.ChatAiSystemSettings.Attach(entity);

        // 원본의 legacy 0/0 조합만 보정하며 일반적으로 저장된 false/유효 지연 값은 보존한다.
        if (entity.ResponseDelayMinMillis == 0 && entity.ResponseDelayMaxMillis == 0)
        {
            Apply(entity, ChatAiSystemPolicy.EnsureLegacyDelay(Map(entity)));
            entity.UpdatedAt = now;
            entity.UpdatedBy = audit;
            await db.SaveChangesAsync(token);
        }
        return entity;
    }

    public static ChatAiSystemSettings Map(ChatAiSystemSettingEntity entity)
    {
        return new()
        {
            MaxAiMembersPerRoom = entity.MaxAiMembersPerRoom,
            ConversationResponseRate = entity.ConversationResponseRate,
            ConversationCooldownSeconds = entity.ConversationCooldownSeconds,
            ConversationMinHumanMessagesAfterAi = entity.ConversationMinHumanMessagesAfterAi,
            ResponseDelayEnabled = entity.ResponseDelayEnabled,
            ResponseDelayMinMillis = entity.ResponseDelayMinMillis,
            ResponseDelayMaxMillis = entity.ResponseDelayMaxMillis,
            RevivalFirstDelayHours = entity.RevivalFirstDelayHours,
            RevivalSecondDelayHours = entity.RevivalSecondDelayHours,
            RevivalThirdDelayHours = entity.RevivalThirdDelayHours,
            RevivalAllowedStartTime = entity.RevivalAllowedStartTime,
            RevivalAllowedEndTime = entity.RevivalAllowedEndTime,
            ContextMaxMessages = entity.ContextMaxMessages,
            ContextMaxCharacters = entity.ContextMaxCharacters,
            ReplyMaxCharacters = entity.ReplyMaxCharacters,
            MentionRateLimitCount = entity.MentionRateLimitCount,
            MentionRateLimitWindowSeconds = entity.MentionRateLimitWindowSeconds,
        };
    }

    public static void Apply(ChatAiSystemSettingEntity entity, ChatAiSystemSettings value)
    {
        entity.MaxAiMembersPerRoom = value.MaxAiMembersPerRoom;
        entity.ConversationResponseRate = value.ConversationResponseRate;
        entity.ConversationCooldownSeconds = value.ConversationCooldownSeconds;
        entity.ConversationMinHumanMessagesAfterAi = value.ConversationMinHumanMessagesAfterAi;
        entity.ResponseDelayEnabled = value.ResponseDelayEnabled;
        entity.ResponseDelayMinMillis = value.ResponseDelayMinMillis;
        entity.ResponseDelayMaxMillis = value.ResponseDelayMaxMillis;
        entity.RevivalFirstDelayHours = value.RevivalFirstDelayHours;
        entity.RevivalSecondDelayHours = value.RevivalSecondDelayHours;
        entity.RevivalThirdDelayHours = value.RevivalThirdDelayHours;
        entity.RevivalAllowedStartTime = value.RevivalAllowedStartTime;
        entity.RevivalAllowedEndTime = value.RevivalAllowedEndTime;
        entity.ContextMaxMessages = value.ContextMaxMessages;
        entity.ContextMaxCharacters = value.ContextMaxCharacters;
        entity.ReplyMaxCharacters = value.ReplyMaxCharacters;
        entity.MentionRateLimitCount = value.MentionRateLimitCount;
        entity.MentionRateLimitWindowSeconds = value.MentionRateLimitWindowSeconds;
    }
}
