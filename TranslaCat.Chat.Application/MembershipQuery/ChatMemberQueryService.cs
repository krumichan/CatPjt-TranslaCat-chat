using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.Presence;

namespace TranslaCat.Chat.Application.MembershipQuery;

public sealed record ChatMemberProfileData(long UserId, string? PublicId, string? Nickname, string? ProfileImageUrl,
    string? ProfileBackgroundImageUrl, string? Bio);
public sealed record ChatGeneralMemberState(long Id, long ChatRoomId, long UserId, string Role, bool Active, DateTime JoinedAt, DateTime? LeftAt);
public sealed record ChatMemberAiDisplay(long AiMemberId, string Nickname, string? ProfileImageUrl, string Role, bool Active, DateTime JoinedAt);
public sealed record ChatMemberQueryState(bool PresenceVisible, IReadOnlyList<ChatGeneralMemberState> Members,
    IReadOnlyList<ChatMemberAiDisplay> AiMembers, string? AiDisclosureType);
public sealed record ChatGeneralMember(ChatGeneralMemberState State, ChatMemberProfileData Profile, bool? Online);
public sealed record ChatGeneralMemberList(IReadOnlyList<ChatGeneralMember> Members, IReadOnlyList<ChatMemberAiDisplay> AiMembers, string? AiDisclosureType);
public sealed record ChatGeneralMemberProfile(ChatMemberProfileData Profile, string FriendStatus, bool? Online);

public interface IChatMemberQueryStore
{
    Task<ChatMemberQueryState> ReadAsync(long userId, long roomId, long? targetUserId, CancellationToken cancellationToken);
}

public interface IChatMemberProfileReader
{
    // 일반 프로필·스토리지·친구 신청 상태는 공통 소유 서비스의 최신 계약이다. CHAT에 계정 DB를 복제하지 않는다.
    Task<ChatMemberProfileData> GetSummaryAsync(long userId, CancellationToken cancellationToken);
    Task<string> GetFriendStatusAsync(long requesterUserId, long targetUserId, CancellationToken cancellationToken);
}

public sealed class ChatMemberQueryService(IChatMemberQueryStore store, IChatMemberProfileReader? profiles, ChatPresenceCoordinator? presence)
{
    public async Task<ChatGeneralMemberList> ListAsync(long userId, long roomId, CancellationToken cancellationToken = default)
    {
        // 멤버십/OPEN 제한을 먼저 적용하고, PRIVATE AI 방은 인간 멤버 Presence 조회 자체를 생략한다.
        var state = await store.ReadAsync(userId, roomId, null, cancellationToken);
        var online = state.PresenceVisible && presence is not null
            ? await presence.ResolveOnlineByUserIdsAsync(state.Members.Select(member => member.UserId), cancellationToken)
            : new Dictionary<long, bool>();
        var members = new List<ChatGeneralMember>();
        foreach (var member in state.Members)
        {
            var profile = await Profiles().GetSummaryAsync(member.UserId, cancellationToken);
            members.Add(new(member, profile, online.TryGetValue(member.UserId, out var value) ? value : null));
        }
        return new(members, state.AiMembers, state.AiDisclosureType);
    }

    public async Task<ChatGeneralMemberProfile> ProfileAsync(long userId, long roomId, long targetUserId, CancellationToken cancellationToken = default)
    {
        var state = await store.ReadAsync(userId, roomId, targetUserId, cancellationToken);
        var profile = await Profiles().GetSummaryAsync(targetUserId, cancellationToken);

        // SELF는 원본의 첫 분기다. 나머지 BLOCKED/FRIEND/신청 상태 우선순위는 공통 소유 서비스가 계산한다.
        var status = userId == targetUserId ? "SELF" : await Profiles().GetFriendStatusAsync(userId, targetUserId, cancellationToken);
        if (status is not ("SELF" or "BLOCKED" or "FRIEND" or "REQUEST_SENT" or "REQUEST_RECEIVED" or "NONE"))
        {
            throw new ChatMembershipDependencyUnavailableException();
        }
        var online = state.PresenceVisible && presence is not null
            ? await presence.ResolveOnlineAsync(targetUserId, cancellationToken) : null;
        return new(profile, status, online);
    }

    private IChatMemberProfileReader Profiles()
    {
        return profiles ?? throw new ChatMembershipDependencyUnavailableException();
    }
}
