using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Application.Membership;

public sealed class ChatMembershipService(IChatMembershipStore store, IChatMembershipDirectory? directory, Func<DateTime> clock)
{
    public async Task<long> CreateOrGetFriendDirectAsync(long userId, long friendUserId, CancellationToken cancellationToken = default)
    {
        // 기존 방 재사용도 현재 친구·차단 권한을 먼저 확인한다.
        if (userId == friendUserId)
        {
            throw Error("자기 자신은 친구 채팅 대상이 될 수 없습니다.", "FRIEND_CHAT_SELF_NOT_ALLOWED");
        }
        if (!await Directory().AreFriendsAsync(userId, friendUserId, cancellationToken))
        {
            throw Error("친구 관계인 사용자와만 친구 채팅을 시작할 수 있습니다.", "FRIEND_RELATION_REQUIRED");
        }
        if (await Directory().IsBlockedBetweenAsync(userId, friendUserId, cancellationToken))
        {
            throw Error("차단 관계가 있는 사용자와 친구 채팅을 시작할 수 없습니다.", "USER_BLOCKED_BETWEEN");
        }
        var actor = await RequireFriendGroupUserAsync(userId, cancellationToken);

        return await store.ExecuteAsync(async (session, token) =>
        {
            var existing = await session.FindFriendDirectAsync(userId, friendUserId, token);
            if (existing is not null)
            {
                return existing.Value;
            }

            // 원본은 없는 쌍에 대한 unique/lock을 제공하지 않는다. 동시 생성의 유일성을 새로 주장하지 않는다.
            var target = await RequireFriendGroupUserAsync(friendUserId, token);
            var now = clock();
            var room = await session.CreateFriendDirectAsync(actor, now, token);
            await session.AddOrRestoreAsync(room, actor, "OWNER", null, actor.Email, now, token);
            await session.AddOrRestoreAsync(room, target, "MEMBER", null, actor.Email, now, token);
            return room.Id;
        }, cancellationToken);
    }

    public Task<ChatInvitationResult> InviteAsync(long userId, long roomId, ChatMembershipTargets targets, CancellationToken cancellationToken = default)
    {
        return store.ExecuteAsync(async (session, token) =>
            {
                // 원본 순서대로 방 lock/type를 확인한 다음 멤버 권한과 대상 사용자를 검증한다.
                var room = await session.LockRoomAsync(roomId, token);
                if (room.RoomType != "GROUP")
                {
                    throw Error("기존 멤버 초대는 그룹 채팅방에서만 가능합니다.", "CHAT_ROOM_INVITE_UNSUPPORTED_ROOM_TYPE");
                }
                if (room.SourceType is "OPEN" or "AI")
                {
                    throw Error("해당 채팅방 타입에서는 멤버를 초대할 수 없습니다.", "CHAT_ROOM_INVITE_UNSUPPORTED_ROOM_TYPE");
                }
                var members = await session.GetActiveMembersAsync(roomId, token);
                var requester = RequireMember(members, userId);
                if (requester.Role is not ("OWNER" or "ADMIN"))
                {
                    throw Error("OWNER 또는 ADMIN만 멤버를 초대할 수 있습니다.", "CHAT_ROOM_INVITE_NOT_ALLOWED");
                }
                var users = await ResolveTargetsAsync(userId, targets, "CHAT_ROOM_INVITE_TARGET_REQUIRED", token);
                if (users.Any(user => members.Any(member => member.UserId == user.Id)))
                {
                    throw Error("이미 채팅방에 참여 중인 사용자입니다.", "CHAT_ROOM_INVITE_ALREADY_MEMBER");
                }

                var actor = await RequireUserAsync(userId, token);
                var cursor = await session.GetLatestSentMessageIdAsync(roomId, token);
                var now = clock();
                var invited = await AddMembersAsync(session, room, actor, users, cursor, now, token);
                var names = string.Join(", ", users.Select(DisplayName));
                var message = await session.InsertSystemMessageAsync(roomId,
                    (actor.Username ?? "null") + "님이 " + names + "님을 초대했습니다.", actor.Email, now, token);

                // 상태 저장과 외부 전달을 분리한다. 원본의 pre-commit SYSTEM 발행을 재현하지 않는다.
                session.RegisterAfterCommit(new ChatMembershipMessageCreated(message));
                RegisterInvitations(session, room, actor, invited, users);
                session.RegisterAfterCommit(new ChatMembershipChanged(roomId, now));
                return new ChatInvitationResult(roomId, false, Responses(invited, users));
            }, cancellationToken);
    }

    public Task<ChatInvitationResult> ConvertAsync(long userId, long roomId, ChatGroupConversionRequest request, CancellationToken cancellationToken = default)
    {
        return store.ExecuteAsync(async (session, token) =>
            {
                var direct = await session.LockRoomAsync(roomId, token);
                if (direct.RoomType != "DIRECT" || direct.SourceType != "FRIEND")
                {
                    throw Error("FRIEND DIRECT 채팅방에서만 새 그룹으로 전환할 수 있습니다.", "CHAT_ROOM_INVITE_UNSUPPORTED_ROOM_TYPE");
                }
                var name = ChatMessageText.Trim(request.Name ?? "");
                var description = NullableText(request.Description);
                ValidateConversionText(name, description);

                var members = await session.GetActiveMembersAsync(roomId, token);
                _ = RequireMember(members, userId);
                if (members.Count != 2)
                {
                    throw Error("유효한 1:1 채팅방 멤버 구성이 아닙니다.", "CHAT_ROOM_DIRECT_MEMBER_INVALID");
                }
                var targets = await ResolveTargetsAsync(userId, request.Targets, "CHAT_ROOM_DIRECT_CONVERSION_TARGET_REQUIRED", token);
                var added = targets.Where(user => members.All(member => member.UserId != user.Id)).ToArray();
                if (added.Length == 0)
                {
                    throw Error("새 그룹 채팅방에 추가할 사용자가 필요합니다.", "CHAT_ROOM_DIRECT_CONVERSION_TARGET_REQUIRED");
                }
                bool allFriends = true;
                foreach (var target in added)
                {
                    if (!await Directory().AreFriendsAsync(userId, target.Id, token))
                    {
                        allFriends = false;
                        break;
                    }
                }

                // 기존 DIRECT는 그대로 두고 새 GROUP을 만든다. 기존 상대도 새 그룹의 초대 수신자다.
                var actor = await RequireUserAsync(userId, token);
                var partner = await RequireUserAsync(members.Single(member => member.UserId != userId).UserId, token);
                var now = clock();
                var group = await session.CreateGroupAsync(name, description, actor, allFriends ? "FRIEND" : "MANUAL", now, token);
                await session.AddOrRestoreAsync(group, actor, "OWNER", null, actor.Email, now, token);
                var invitedUsers = new[] { partner }.Concat(added).DistinctBy(user => user.Id).ToArray();
                var invited = await AddMembersAsync(session, group, actor, invitedUsers, null, now, token);
                RegisterInvitations(session, group, actor, invited, invitedUsers);
                return new ChatInvitationResult(group.Id, true, Responses(invited, invitedUsers));
            }, cancellationToken);
    }

    public async Task<long> CreateFriendGroupAsync(long userId, ChatFriendGroupRequest request, CancellationToken cancellationToken = default)
    {
        if (request.MemberUserIds is null || request.MemberUserIds.Count == 0)
        {
            throw Error("친구 그룹 채팅 멤버는 최소 1명 이상 필요합니다.", "FRIEND_GROUP_MEMBER_REQUIRED");
        }
        var ids = request.MemberUserIds.Distinct().ToArray();
        if (ids.Contains(userId))
        {
            throw Error("자기 자신은 친구 그룹 채팅 멤버 목록에 포함할 수 없습니다.", "FRIEND_GROUP_SELF_MEMBER_NOT_ALLOWED");
        }
        foreach (var id in ids)
        {
            if (id is null)
            {
                throw Error("대상 사용자 ID는 필수입니다.", "FRIEND_CHAT_TARGET_USER_ID_REQUIRED");
            }
            if (!await Directory().AreFriendsAsync(userId, id.Value, cancellationToken))
            {
                throw Error("친구 관계인 사용자와만 친구 채팅을 시작할 수 있습니다.", "FRIEND_RELATION_REQUIRED");
            }
            if (await Directory().IsBlockedBetweenAsync(userId, id.Value, cancellationToken))
            {
                throw Error("차단 관계가 있는 사용자와 친구 채팅을 시작할 수 없습니다.", "USER_BLOCKED_BETWEEN");
            }
        }
        // 친구 GROUP의 UserService.getById 실패는 원본 일반 오류이며 초대 전용 business code와 다르다.
        var actor = await RequireFriendGroupUserAsync(userId, cancellationToken);
        var users = new List<ChatMembershipUser>();
        foreach (var id in ids)
        {
            users.Add(await RequireFriendGroupUserAsync(id!.Value, cancellationToken));
        }

        return await store.ExecuteAsync(async (session, token) =>
        {
            // 친구 GROUP 생성은 원본대로 이름/설명을 trim하지 않고, 초대 notification/SYSTEM을 새로 추가하지 않는다.
            var now = clock();
            var group = await session.CreateGroupAsync(request.Name, request.Description, actor, "FRIEND", now, token);
            await session.AddOrRestoreAsync(group, actor, "OWNER", null, actor.Email, now, token);
            await AddMembersAsync(session, group, actor, users, null, now, token);
            return group.Id;
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<ChatMembershipUser>> ResolveTargetsAsync(long userId, ChatMembershipTargets targets, string emptyCode, CancellationToken token)
    {
        var users = new Dictionary<long, ChatMembershipUser>();
        foreach (var id in targets.TargetUserIds ?? [])
        {
            var user = id is null ? null : await Directory().FindByIdAsync(id.Value, token);
            if (user is null)
            {
                throw TargetNotFound();
            }
            users[user.Id] = user;
        }
        foreach (var publicId in targets.TargetPublicIds ?? [])
        {
            var normalized = NullableText(publicId);
            var user = normalized is null ? null : await Directory().FindByPublicIdAsync(normalized, token);
            if (user is null)
            {
                throw TargetNotFound();
            }
            users[user.Id] = user;
        }
        if (users.Count == 0)
        {
            throw Error("초대 대상 사용자는 최소 1명 이상 필요합니다.", emptyCode);
        }
        foreach (var user in users.Values)
        {
            if (user.Id == userId)
            {
                throw Error("자기 자신을 초대할 수 없습니다.", "CHAT_ROOM_INVITE_SELF_NOT_ALLOWED");
            }
            if (await Directory().IsBlockedBetweenAsync(userId, user.Id, token))
            {
                throw Error("차단 관계인 사용자는 초대할 수 없습니다.", "CHAT_ROOM_INVITE_TARGET_BLOCKED");
            }
        }
        return users.Values.ToArray();
    }

    private static async Task<IReadOnlyList<ChatMembershipMember>> AddMembersAsync(IChatMembershipSession session, ChatMembershipRoom room,
        ChatMembershipUser actor, IEnumerable<ChatMembershipUser> users, long? cursor, DateTime now, CancellationToken token)
    {
        var members = new List<ChatMembershipMember>();
        foreach (var user in users)
        {
            members.Add(await session.AddOrRestoreAsync(room, user, "MEMBER", cursor, actor.Email, now, token));
        }
        return members;
    }

    private static void RegisterInvitations(IChatMembershipSession session, ChatMembershipRoom room, ChatMembershipUser actor,
        IReadOnlyList<ChatMembershipMember> members, IReadOnlyList<ChatMembershipUser> users)
    {
        foreach (var member in members)
        {
            var user = users.Single(value => value.Id == member.UserId);
            session.RegisterAfterCommit(new ChatMembershipInvitationCommitted(room.Id, room.Name, user.Id, user.Email,
                actor.Id, member.Id, member.JoinedAt, actor.Email));
        }
    }

    private static IReadOnlyList<ChatInvitedMember> Responses(IReadOnlyList<ChatMembershipMember> members, IReadOnlyList<ChatMembershipUser> users)
    {
        return members.Select(member =>
            {
                var user = users.Single(value => value.Id == member.UserId);
                return new ChatInvitedMember(user.Id, user.PublicId, user.Nickname, user.ProfileImageUrl, member.JoinedAt);
            }).ToArray();
    }

    private static ChatMembershipMember RequireMember(IReadOnlyList<ChatMembershipMember> members, long userId)
    {
        return members.SingleOrDefault(member => member.UserId == userId)
                ?? throw Error("채팅방 멤버가 아니거나 접근 권한이 없습니다.", "CHAT_ROOM_MEMBER_ACCESS_DENIED");
    }

    private IChatMembershipDirectory Directory()
    {
        return directory ?? throw new ChatMembershipDependencyUnavailableException();
    }

    private async Task<ChatMembershipUser> RequireUserAsync(long userId, CancellationToken token)
    {
        return await Directory().FindByIdAsync(userId, token) ?? throw TargetNotFound();
    }

    private async Task<ChatMembershipUser> RequireFriendGroupUserAsync(long userId, CancellationToken token)
    {
        return await Directory().FindByIdAsync(userId, token) ?? throw new ArgumentException("User was not found.");
    }

    private static string DisplayName(ChatMembershipUser user)
    {
        return NullableText(user.Nickname) is not null ? user.Nickname!
            : NullableText(user.Username) is not null ? user.Username! : user.PublicId;
    }

    private static string? NullableText(string? value)
    {
        return value is null || ChatMessageText.Trim(value).Length == 0 ? null : ChatMessageText.Trim(value);
    }

    private static ChatMembershipException Error(string message, string code)
    {
        return new(message, code);
    }

    private static ChatMembershipException TargetNotFound()
    {
        return Error("초대 대상 사용자를 찾을 수 없습니다.", "CHAT_ROOM_INVITE_TARGET_NOT_FOUND");
    }

    private static void ValidateConversionText(string name, string? description)
    {
        if (name.Length == 0)
        {
            throw Error("그룹 이름은 필수입니다.", "CHAT_ROOM_GROUP_NAME_REQUIRED");
        }
        if (name.Length > 100)
        {
            throw Error("그룹 이름은 100자 이하로 입력해주세요.", "CHAT_ROOM_GROUP_NAME_TOO_LONG");
        }
        if (description?.Length > 500)
        {
            throw Error("그룹 설명은 500자 이하로 입력해주세요.", "CHAT_ROOM_GROUP_DESCRIPTION_TOO_LONG");
        }
    }
}
