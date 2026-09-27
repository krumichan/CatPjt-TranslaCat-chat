using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.MembershipQuery;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Application.Presence;
using TranslaCat.Chat.Application.ProfileImages;
using TranslaCat.Chat.Application.Rooms;

namespace TranslaCat.Chat.Infrastructure.Core;

public sealed class HttpChatCoreDirectory(HttpChatCoreClient client) : IChatMessageProfileReader, IChatRoomAccountReader,
    IChatMembershipDirectory, IChatMemberProfileReader, IChatNotificationProfileReader, IChatPresenceProfileReader, IChatAiUserNameReader
{
    public async Task<ChatUserMessageProfile> GetUserAsync(long userId, CancellationToken token)
    {
        var account = await RequiredAsync(userId, token);
        // 메시지는 nickname이 아니라 원본 User.username과 email을 보존한다.
        return new(account.UserId, account.Username, account.Email, account.Profile?.ProfileImageUrl);
    }

    public async Task<string> GetAuditIdentityAsync(long userId, CancellationToken token)
    {
        return (await RequiredAsync(userId, token)).Email;
    }

    public async Task EnsureUserExistsAsync(long? userId, CancellationToken token)
    {
        if (userId is null)
        {
            throw new ChatRoomException("사용자 ID는 필수입니다.", "USER_ID_REQUIRED");
        }

        if (await FindAccountAsync(userId.Value, null, token) is null)
        {
            throw new ChatRoomException("사용자를 찾을 수 없습니다.", "USER_NOT_FOUND");
        }
    }

    public async Task<ChatDirectPartner> GetDirectPartnerAsync(long userId, CancellationToken token)
    {
        var account = await RequiredAsync(userId, token);
        var profile = account.Profile;
        var display = HasText(profile?.Nickname) ? profile!.Nickname : Fallback(account);
        return new(account.UserId, account.PublicId, display, profile?.ProfileImageUrl, profile?.ProfileBackgroundImageUrl,
            HasText(profile?.Bio) ? profile!.Bio : null, null);
    }

    public async Task<ChatMembershipUser?> FindByIdAsync(long userId, CancellationToken token)
    {
        return Membership(await FindAccountAsync(userId, null, token));
    }

    public async Task<ChatMembershipUser?> FindByPublicIdAsync(string publicId, CancellationToken token)
    {
        return Membership(await FindAccountAsync(null, publicId, token));
    }

    public async Task<bool> AreFriendsAsync(long userId, long targetUserId, CancellationToken token)
    {
        return (await RelationAsync(userId, targetUserId, token)).Friends;
    }

    public async Task<bool> IsBlockedBetweenAsync(long userId, long targetUserId, CancellationToken token)
    {
        return (await RelationAsync(userId, targetUserId, token)).Blocked;
    }

    public async Task<ChatMemberProfileData> GetSummaryAsync(long userId, CancellationToken token)
    {
        var account = await RequiredAsync(userId, token);
        var summary = account.Summary;
        return new(summary.UserId, summary.PublicId, summary.Nickname, summary.ProfileImageUrl, summary.ProfileBackgroundImageUrl, summary.Bio);
    }

    public async Task<string> GetFriendStatusAsync(long requesterUserId, long targetUserId, CancellationToken token)
    {
        return (await RelationAsync(requesterUserId, targetUserId, token)).FriendStatus;
    }

    public async Task<ChatNotificationUserDisplay> GetDisplayAsync(long userId, CancellationToken token)
    {
        var account = await RequiredAsync(userId, token);
        // 알림은 profile 존재 여부로 분기한다. default summary의 email-prefix fallback을 쓰지 않는다.
        return new(account.Profile is { } profile ? profile.Nickname : Fallback(account), account.Profile?.ProfileImageUrl);
    }

    public async Task<string?> FindPublicIdAsync(long userId, CancellationToken token)
    {
        return (await FindAccountAsync(userId, null, token))?.PublicId;
    }

    public async Task<string?> GetUserNameAsync(long userId, CancellationToken token)
    {
        return (await FindAccountAsync(userId, null, token))?.Username;
    }

    public async Task<string?> ResolveObjectUrlAsync(string objectKey, CancellationToken token)
    {
        var result = await client.UrlsAsync([objectKey], token);
        if (result.Objects is not { Count: 1 }
            || result.Objects[0] is null
            || result.Objects[0].ObjectKey != objectKey)
        {
            throw new ChatCoreUnavailableException();
        }

        return result.Objects[0].Url;
    }

    private async Task<ChatCoreAccount> RequiredAsync(long userId, CancellationToken token)
    {
        return await FindAccountAsync(userId, null, token) ?? throw new ChatCoreUnavailableException();
    }

    private async Task<ChatCoreAccount?> FindAccountAsync(long? userId, string? publicId, CancellationToken token)
    {
        // 한 계정의 응답만 소비한다. 입력과 상관없는 계정이나 모호한 부재 응답은 채택하지 않는다.
        if (userId is <= 0 || (userId is null && string.IsNullOrWhiteSpace(publicId)))
        {
            throw new ChatCoreUnavailableException();
        }

        var result = await client.AccountsAsync(userId is { } id ? [id] : [], publicId is not null ? [publicId] : [], token);
        if (result.Accounts is null || result.MissingUserIds is null || result.MissingPublicIds is null)
        {
            throw new ChatCoreUnavailableException();
        }

        if (result.Accounts.Count == 0)
        {
            bool missing = userId is { } requested
                ? result.MissingUserIds.SequenceEqual([requested]) && result.MissingPublicIds.Count == 0
                : result.MissingPublicIds.SequenceEqual([publicId!]) && result.MissingUserIds.Count == 0;
            if (!missing)
            {
                throw new ChatCoreUnavailableException();
            }

            return null;
        }

        // profile null은 원본 상태다. 계정과 summary의 식별자 불일치는 원격 계약 오류다.
        if (result.Accounts.Count != 1 || result.MissingUserIds.Count != 0 || result.MissingPublicIds.Count != 0)
        {
            throw new ChatCoreUnavailableException();
        }

        var account = result.Accounts[0];
        if (account is null
            || account.UserId <= 0
            || string.IsNullOrWhiteSpace(account.Email)
            || string.IsNullOrWhiteSpace(account.PublicId)
            || account.Summary is null
            || account.Summary.UserId != account.UserId
            || account.Summary.PublicId != account.PublicId
            || account.Summary.Nickname is null
            || (userId is { } expected && account.UserId != expected)
            || (publicId is not null && account.PublicId != publicId))
        {
            throw new ChatCoreUnavailableException();
        }

        return account;
    }

    private async Task<ChatCoreRelation> RelationAsync(long requester, long target, CancellationToken token)
    {
        if (requester <= 0 || target <= 0)
        {
            throw new ChatCoreUnavailableException();
        }

        // 관계 계산은 Core가 소유한다. 요청 대상과 기존 enum만 확인하고 다른 정책을 재구현하지 않는다.
        var result = await client.RelationsAsync(requester, [target], token);
        if (result.Relations is not { Count: 1 }
            || result.MissingUserIds is not { Count: 0 }
            || result.Relations[0] is null
            || result.Relations[0].TargetUserId != target
            || result.Relations[0].FriendStatus is not
                ("SELF" or "BLOCKED" or "FRIEND" or "REQUEST_SENT" or "REQUEST_RECEIVED" or "NONE"))
        {
            throw new ChatCoreUnavailableException();
        }

        return result.Relations[0];
    }

    private static ChatMembershipUser? Membership(ChatCoreAccount? account)
    {
        return account is null ? null : new(account.UserId, account.Email, account.Username, account.PublicId,
                account.Summary.Nickname, account.Summary.ProfileImageUrl);
    }

    private static string? Fallback(ChatCoreAccount account)
    {
        return HasText(account.Username) ? account.Username : account.PublicId;
    }

    private static bool HasText(string? value)
    {
        return !ChatProfileImagePolicy.IsJavaBlank(value);
    }
}
