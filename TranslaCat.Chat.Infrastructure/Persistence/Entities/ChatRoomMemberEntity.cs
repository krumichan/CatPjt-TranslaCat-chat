namespace TranslaCat.Chat.Infrastructure.Persistence.Entities;

// 영속 상태만 표현한다. 사용자 정보 조회와 업무 정책은 각 port/use case가 담당한다.
public sealed class ChatRoomMemberEntity
{
    public long Id
    {
        get; set;
    }
    public long ChatRoomId
    {
        get; set;
    }
    public long UserId
    {
        get; set;
    }
    public string Role { get; set; } = null!;
    public string? OriginalLanguageCode
    {
        get; set;
    }
    public string? TranslationLanguageCode
    {
        get; set;
    }
    public bool ShowOriginal { get; set; } = true;
    public bool ShowTranslation { get; set; } = true;
    public bool Active { get; set; } = true;
    public DateTime JoinedAt
    {
        get; set;
    }
    public long? LastReadMessageId
    {
        get; set;
    }
    public DateTime? LastReadAt
    {
        get; set;
    }
    public DateTime? LeftAt
    {
        get; set;
    }
    public DateTime? DeletedAt
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
