namespace TranslaCat.Chat.Infrastructure.Persistence.Entities;

// 영속 상태만 표현한다. 사용자 정보 조회와 업무 정책은 각 port/use case가 담당한다.
public sealed class ChatMessageTranslationEntity
{
    public long Id
    {
        get; set;
    }
    public long ChatMessageId
    {
        get; set;
    }
    public string LanguageCode { get; set; } = null!;
    public string? TranslatedContent
    {
        get; set;
    }
    public string Status { get; set; } = null!;
    public string? FailureReason
    {
        get; set;
    }
    public DateTime? CompletedAt
    {
        get; set;
    }
    public DateTime? DeletedAt
    {
        get; set;
    }

    // 외부 번역 호출 동안 DB transaction을 잡지 않기 위한 CHAT 전용 처리 lease다.
    public string? ProcessingToken
    {
        get; set;
    }
    public DateTime? ProcessingExpiresAt
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
