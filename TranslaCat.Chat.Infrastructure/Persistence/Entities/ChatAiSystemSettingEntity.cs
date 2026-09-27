namespace TranslaCat.Chat.Infrastructure.Persistence.Entities;

// 영속 상태만 표현한다. 사용자 정보 조회와 업무 정책은 각 port/use case가 담당한다.
public sealed class ChatAiSystemSettingEntity
{
    public string Id { get; set; } = null!;
    public int MaxAiMembersPerRoom
    {
        get; set;
    }
    public int ConversationResponseRate
    {
        get; set;
    }
    public int ConversationCooldownSeconds
    {
        get; set;
    }
    public int ConversationMinHumanMessagesAfterAi
    {
        get; set;
    }
    public bool ResponseDelayEnabled
    {
        get; set;
    }
    public int ResponseDelayMinMillis
    {
        get; set;
    }
    public int ResponseDelayMaxMillis
    {
        get; set;
    }
    public int RevivalFirstDelayHours
    {
        get; set;
    }
    public int RevivalSecondDelayHours
    {
        get; set;
    }
    public int RevivalThirdDelayHours
    {
        get; set;
    }
    public TimeSpan RevivalAllowedStartTime
    {
        get; set;
    }
    public TimeSpan RevivalAllowedEndTime
    {
        get; set;
    }
    public int ContextMaxMessages
    {
        get; set;
    }
    public int ContextMaxCharacters
    {
        get; set;
    }
    public int ReplyMaxCharacters
    {
        get; set;
    }
    public int MentionRateLimitCount
    {
        get; set;
    }
    public int MentionRateLimitWindowSeconds
    {
        get; set;
    }

    // 원본의 감사 필드를 보존하며 시각이나 인증 주체를 여기서 추측하지 않는다.
    public string? CreatedBy
    {
        get; set;
    }
    public DateTime CreatedAt
    {
        get; set;
    }
    public string? UpdatedBy
    {
        get; set;
    }
    public DateTime UpdatedAt
    {
        get; set;
    }
}
