namespace TranslaCat.Chat.Application.AiManagement;

public sealed record ChatAiSystemSettings
{
    public int MaxAiMembersPerRoom
    {
        get; init;
    }
    public int ConversationResponseRate
    {
        get; init;
    }
    public int ConversationCooldownSeconds
    {
        get; init;
    }
    public int ConversationMinHumanMessagesAfterAi
    {
        get; init;
    }
    public bool ResponseDelayEnabled
    {
        get; init;
    }
    public int ResponseDelayMinMillis
    {
        get; init;
    }
    public int ResponseDelayMaxMillis
    {
        get; init;
    }
    public int RevivalFirstDelayHours
    {
        get; init;
    }
    public int RevivalSecondDelayHours
    {
        get; init;
    }
    public int RevivalThirdDelayHours
    {
        get; init;
    }
    public TimeSpan RevivalAllowedStartTime
    {
        get; init;
    }
    public TimeSpan RevivalAllowedEndTime
    {
        get; init;
    }
    public int ContextMaxMessages
    {
        get; init;
    }
    public int ContextMaxCharacters
    {
        get; init;
    }
    public int ReplyMaxCharacters
    {
        get; init;
    }
    public int MentionRateLimitCount
    {
        get; init;
    }
    public int MentionRateLimitWindowSeconds
    {
        get; init;
    }
}

public sealed record ChatAiSystemPatch
{
    public int? MaxAiMembersPerRoom
    {
        get; init;
    }
    public int? ConversationResponseRate
    {
        get; init;
    }
    public int? ConversationCooldownSeconds
    {
        get; init;
    }
    public int? ConversationMinHumanMessagesAfterAi
    {
        get; init;
    }
    public bool? ResponseDelayEnabled
    {
        get; init;
    }
    public int? ResponseDelayMinMillis
    {
        get; init;
    }
    public int? ResponseDelayMaxMillis
    {
        get; init;
    }
    public int? RevivalFirstDelayHours
    {
        get; init;
    }
    public int? RevivalSecondDelayHours
    {
        get; init;
    }
    public int? RevivalThirdDelayHours
    {
        get; init;
    }
    public TimeSpan? RevivalAllowedStartTime
    {
        get; init;
    }
    public TimeSpan? RevivalAllowedEndTime
    {
        get; init;
    }
    public int? ContextMaxMessages
    {
        get; init;
    }
    public int? ContextMaxCharacters
    {
        get; init;
    }
    public int? ReplyMaxCharacters
    {
        get; init;
    }
    public int? MentionRateLimitCount
    {
        get; init;
    }
    public int? MentionRateLimitWindowSeconds
    {
        get; init;
    }
}

public static class ChatAiSystemPolicy
{
    public static ChatAiSystemSettings Defaults => new()
    {
        MaxAiMembersPerRoom = 2,
        ConversationResponseRate = 15,
        ConversationCooldownSeconds = 180,
        ConversationMinHumanMessagesAfterAi = 2,
        ResponseDelayEnabled = true,
        ResponseDelayMinMillis = 1200,
        ResponseDelayMaxMillis = 3500,
        RevivalFirstDelayHours = 24,
        RevivalSecondDelayHours = 72,
        RevivalThirdDelayHours = 168,
        RevivalAllowedStartTime = TimeSpan.FromHours(10),
        RevivalAllowedEndTime = TimeSpan.FromHours(22),
        ContextMaxMessages = 30,
        ContextMaxCharacters = 12000,
        ReplyMaxCharacters = 800,
        MentionRateLimitCount = 5,
        MentionRateLimitWindowSeconds = 60,
    };

    public static ChatAiSystemSettings EnsureLegacyDelay(ChatAiSystemSettings current)
    {
        return current.ResponseDelayMinMillis == 0 && current.ResponseDelayMaxMillis == 0
            ? current with
            {
                ResponseDelayEnabled = true,
                ResponseDelayMinMillis = 1200,
                ResponseDelayMaxMillis = 3500
            }
            : current;
    }

    public static ChatAiSystemSettings Merge(ChatAiSystemSettings current, ChatAiSystemPatch patch)
    {
        // null은 기존 값을 유지한다. 먼저 전체 병합 상태를 검증하므로 부분 저장이 생기지 않는다.
        var merged = current with
        {
            MaxAiMembersPerRoom = patch.MaxAiMembersPerRoom ?? current.MaxAiMembersPerRoom,
            ConversationResponseRate = patch.ConversationResponseRate ?? current.ConversationResponseRate,
            ConversationCooldownSeconds = patch.ConversationCooldownSeconds ?? current.ConversationCooldownSeconds,
            ConversationMinHumanMessagesAfterAi = patch.ConversationMinHumanMessagesAfterAi ?? current.ConversationMinHumanMessagesAfterAi,
            ResponseDelayEnabled = patch.ResponseDelayEnabled ?? current.ResponseDelayEnabled,
            ResponseDelayMinMillis = patch.ResponseDelayMinMillis ?? current.ResponseDelayMinMillis,
            ResponseDelayMaxMillis = patch.ResponseDelayMaxMillis ?? current.ResponseDelayMaxMillis,
            RevivalFirstDelayHours = patch.RevivalFirstDelayHours ?? current.RevivalFirstDelayHours,
            RevivalSecondDelayHours = patch.RevivalSecondDelayHours ?? current.RevivalSecondDelayHours,
            RevivalThirdDelayHours = patch.RevivalThirdDelayHours ?? current.RevivalThirdDelayHours,
            RevivalAllowedStartTime = patch.RevivalAllowedStartTime ?? current.RevivalAllowedStartTime,
            RevivalAllowedEndTime = patch.RevivalAllowedEndTime ?? current.RevivalAllowedEndTime,
            ContextMaxMessages = patch.ContextMaxMessages ?? current.ContextMaxMessages,
            ContextMaxCharacters = patch.ContextMaxCharacters ?? current.ContextMaxCharacters,
            ReplyMaxCharacters = patch.ReplyMaxCharacters ?? current.ReplyMaxCharacters,
            MentionRateLimitCount = patch.MentionRateLimitCount ?? current.MentionRateLimitCount,
            MentionRateLimitWindowSeconds = patch.MentionRateLimitWindowSeconds ?? current.MentionRateLimitWindowSeconds,
        };
        Validate(merged);
        return merged;
    }

    private static void Validate(ChatAiSystemSettings value)
    {
        if (value.MaxAiMembersPerRoom < 1)
        {
            throw Invalid("방당 AI 최대 인원은 1명 이상이어야 합니다.");
        }

        if (value.ConversationResponseRate is < 0 or > 100)
        {
            throw Invalid("CONVERSATION 반응률은 0~100 사이여야 합니다.");
        }

        if (value.ConversationCooldownSeconds < 0)
        {
            throw Invalid("CONVERSATION Cooldown은 0초 이상이어야 합니다.");
        }

        if (value.ConversationMinHumanMessagesAfterAi < 1)
        {
            throw Invalid("AI 발화 후 최소 사람 메시지 수는 1개 이상이어야 합니다.");
        }

        // 지연/시간창은 원본 범위를 유지한다. 배치 편의를 위해 비용 또는 발화 주기를 바꾸지 않는다.
        if (value.ResponseDelayMinMillis < 100 || value.ResponseDelayMaxMillis < 100
            || value.ResponseDelayMinMillis > value.ResponseDelayMaxMillis || value.ResponseDelayMaxMillis > 10000)
        {
            throw Invalid("AI 응답 지연은 100~10000ms 범위에서 최소값이 최대값 이하여야 합니다.");
        }

        if (value.RevivalFirstDelayHours < 1 || value.RevivalSecondDelayHours < 1 || value.RevivalThirdDelayHours < 1)
        {
            throw Invalid("REVIVAL 대기 시간은 1시간 이상이어야 합니다.");
        }

        if (value.RevivalAllowedStartTime >= value.RevivalAllowedEndTime)
        {
            throw Invalid("REVIVAL 허용 시작 시간은 종료 시간보다 빨라야 합니다.");
        }

        if (value.ContextMaxMessages < 1 || value.ContextMaxCharacters < 1 || value.ReplyMaxCharacters < 1)
        {
            throw Invalid("AI Context 및 응답 제한값은 1 이상이어야 합니다.");
        }

        if (value.MentionRateLimitCount < 1 || value.MentionRateLimitWindowSeconds < 1)
        {
            throw Invalid("MENTION Rate Limit 값은 1 이상이어야 합니다.");
        }
    }

    private static ChatAiManagementException Invalid(string message)
    {
        return new(message, "CHAT_AI_SETTING_INVALID");
    }
}

public sealed class ChatAiManagementException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}
public sealed class ChatAiManagementUnavailableException : Exception;

