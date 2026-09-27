namespace TranslaCat.Chat.Infrastructure.Persistence.Entities;

// 영속 상태만 표현한다. 사용자 정보 조회와 업무 정책은 각 port/use case가 담당한다.
public sealed class OpenChatBanEntity
{
    public long Id
    {
        get; set;
    }
    public long ChatRoomId
    {
        get; set;
    }
    public long TargetUserId
    {
        get; set;
    }
    public long TargetChatRoomMemberId
    {
        get; set;
    }
    public string TargetMemberCode { get; set; } = null!;
    public string NicknameSnapshot { get; set; } = null!;
    public string? ProfileImageObjectKeySnapshot
    {
        get; set;
    }
    public DateTime LastJoinedAtSnapshot
    {
        get; set;
    }
    public string TargetRoleSnapshot { get; set; } = null!;
    public long BannedByMemberId
    {
        get; set;
    }
    public string BannedByRole { get; set; } = null!;
    public DateTime BannedAt
    {
        get; set;
    }
    public string Reason { get; set; } = null!;
    public long? ReleasedByMemberId
    {
        get; set;
    }
    public DateTime? ReleasedAt
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
