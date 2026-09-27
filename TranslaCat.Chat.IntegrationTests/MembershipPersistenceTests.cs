using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Infrastructure.Persistence.Membership;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class MembershipPersistenceTests(ChatMySqlFixture database)
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("manual", false)]
    [InlineData("inactive", false)]
    [InlineData("third-member", false)]
    public async Task Friend_DIRECT_reuses_only_active_FRIEND_room_with_exactly_two_active_members(string state, bool reuse)
    {
        // 준비
        var room = await SeedOwnerAsync("DIRECT", state == "manual" ? "MANUAL" : "FRIEND");
        await using (var context = await database.Contexts.CreateDbContextAsync())
        {
            context.ChatRoomMembers.Add(new ChatRoomMemberEntity
            {
                ChatRoomId = room.RoomId,
                UserId = room.UserId + 1,
                Role = "MEMBER",
                Active = state != "inactive",
                JoinedAt = ChatMySqlFixture.Epoch,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            if (state == "third-member")
            {
                context.ChatRoomMembers.Add(new ChatRoomMemberEntity
                {
                    ChatRoomId = room.RoomId,
                    UserId = room.UserId + 2,
                    Role = "MEMBER",
                    JoinedAt = ChatMySqlFixture.Epoch,
                    CreatedAt = ChatMySqlFixture.Epoch,
                    UpdatedAt = ChatMySqlFixture.Epoch
                });
            }
            await context.SaveChangesAsync();
        }
        var delivery = new MembershipDelivery(database);
        var service = Service(new MembershipDirectory(room.UserId, room.UserId + 1), delivery);

        // 실행
        var result = await service.CreateOrGetFriendDirectAsync(room.UserId, room.UserId + 1);
        var repeated = await service.CreateOrGetFriendDirectAsync(room.UserId, room.UserId + 1);

        // 검증: 순차 재호출의 재사용을 검증한다. 원본에 없는 동시 생성 유일성을 주장하지 않는다.
        Assert.Equal(reuse, result == room.RoomId);
        Assert.Equal(result, repeated);
        Assert.Empty(delivery.Delivered);
        await using var verification = await database.Contexts.CreateDbContextAsync();
        var stored = await verification.ChatRooms.SingleAsync(value => value.Id == result);
        Assert.Equal("DIRECT", stored.RoomType);
        Assert.Equal("FRIEND", stored.SourceType);
        Assert.Equal(2, await verification.ChatRoomMembers.CountAsync(value => value.ChatRoomId == result && value.Active));
    }

    [Fact]
    public async Task Invitation_restores_member_initializes_latest_cursor_and_delivers_only_after_commit()
    {
        // 준비
        var room = await SeedOwnerAsync();
        var directory = new MembershipDirectory(room.UserId, room.UserId + 1);
        long previousMemberId;
        await using (var context = await database.Contexts.CreateDbContextAsync())
        {
            var previous = new ChatRoomMemberEntity
            {
                ChatRoomId = room.RoomId,
                UserId = room.UserId + 1,
                Role = "ADMIN",
                Active = false,
                JoinedAt = ChatMySqlFixture.Epoch.AddDays(-1),
                LeftAt = ChatMySqlFixture.Epoch,
                DeletedAt = ChatMySqlFixture.Epoch,
                LastReadMessageId = room.FirstId,
                LastReadAt = ChatMySqlFixture.Epoch.AddDays(-1),
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            };
            context.ChatRoomMembers.Add(previous);
            context.UserChatLanguageSettings.Add(new UserChatLanguageSettingEntity
            {
                UserId = previous.UserId,
                OriginalLanguageCode = "en",
                TranslationLanguageCode = "fr",
                ShowOriginal = false,
                ShowTranslation = true,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            await context.SaveChangesAsync();
            previousMemberId = previous.Id;
        }
        var delivery = new MembershipDelivery(database);
        var service = Service(directory, delivery);

        // 실행: 중복 ID/publicId는 최초 위치를 유지한 한 명으로 합친다.
        var response = await service.InviteAsync(room.UserId, room.RoomId, new([room.UserId + 1, room.UserId + 1], [directory.Users[room.UserId + 1].PublicId]));

        // 검증
        Assert.False(response.CreatedNewGroupRoom);
        Assert.Single(response.InvitedMembers);
        Assert.Equal(3, delivery.Delivered.Count);
        await using var verification = await database.Contexts.CreateDbContextAsync();
        var member = await verification.ChatRoomMembers.SingleAsync(row => row.Id == previousMemberId);
        Assert.Equal("MEMBER", member.Role);
        Assert.True(member.Active);
        Assert.Null(member.LeftAt);
        Assert.Null(member.DeletedAt);
        Assert.Equal(room.SecondId, member.LastReadMessageId);
        Assert.Equal(ChatMySqlFixture.Epoch.AddHours(1), member.LastReadAt);
        Assert.Equal("en", member.OriginalLanguageCode);
        Assert.False(member.ShowOriginal);
        Assert.Equal(1, await verification.ChatMessages.CountAsync(row => row.ChatRoomId == room.RoomId && row.MessageType == "SYSTEM"));
        Assert.True(delivery.ObservedCommittedRows);
    }

    [Theory]
    [InlineData(true, "FRIEND")]
    [InlineData(false, "MANUAL")]
    public async Task Conversion_keeps_original_DIRECT_and_creates_new_GROUP_with_partner_and_new_target(bool friends, string sourceType)
    {
        // 준비
        var room = await SeedOwnerAsync("DIRECT", "FRIEND");
        var directory = new MembershipDirectory(room.UserId, room.UserId + 1, room.UserId + 2) { Friends = friends };
        await using (var context = await database.Contexts.CreateDbContextAsync())
        {
            context.ChatRoomMembers.Add(new ChatRoomMemberEntity
            {
                ChatRoomId = room.RoomId,
                UserId = room.UserId + 1,
                Role = "MEMBER",
                JoinedAt = ChatMySqlFixture.Epoch,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            await context.SaveChangesAsync();
        }
        var delivery = new MembershipDelivery(database);

        // 실행
        var result = await Service(directory, delivery).ConvertAsync(room.UserId, room.RoomId,
            new(" 새 그룹 ", " 설명 ", new([room.UserId + 1, room.UserId + 2], null)));

        // 검증
        Assert.True(result.CreatedNewGroupRoom);
        Assert.NotEqual(room.RoomId, result.RoomId);
        Assert.Equal(2, result.InvitedMembers.Count);
        Assert.All(delivery.Delivered, value => Assert.IsType<ChatMembershipInvitationCommitted>(value));
        await using var verification = await database.Contexts.CreateDbContextAsync();
        Assert.Equal("DIRECT", (await verification.ChatRooms.SingleAsync(row => row.Id == room.RoomId)).RoomType);
        var created = await verification.ChatRooms.SingleAsync(row => row.Id == result.RoomId);
        Assert.Equal(sourceType, created.SourceType);
        Assert.Equal("새 그룹", created.Name);
        Assert.Equal(3, await verification.ChatRoomMembers.CountAsync(row => row.ChatRoomId == result.RoomId && row.Active));
        Assert.Equal(0, await verification.ChatMessages.CountAsync(row => row.ChatRoomId == result.RoomId));
    }

    [Theory]
    [InlineData("permission", "CHAT_ROOM_INVITE_NOT_ALLOWED")]
    [InlineData("blocked", "CHAT_ROOM_INVITE_TARGET_BLOCKED")]
    [InlineData("self", "CHAT_ROOM_INVITE_SELF_NOT_ALLOWED")]
    [InlineData("missing", "CHAT_ROOM_INVITE_TARGET_NOT_FOUND")]
    [InlineData("empty", "CHAT_ROOM_INVITE_TARGET_REQUIRED")]
    public async Task Invalid_invitation_is_rejected_without_member_or_system_message(string denied, string code)
    {
        // 준비
        var room = await SeedOwnerAsync();
        var directory = new MembershipDirectory(room.UserId, room.UserId + 1) { Blocked = denied == "blocked" };
        if (denied == "permission")
        {
            await using var context = await database.Contexts.CreateDbContextAsync();
            var member = await context.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId);
            member.Role = "MEMBER";
            await context.SaveChangesAsync();
        }
        var target = denied == "self" ? room.UserId : denied == "missing" ? room.UserId + 5 : room.UserId + 1;
        var delivery = new MembershipDelivery(database);

        // 실행 / 검증
        var error = await Assert.ThrowsAsync<ChatMembershipException>(() => Service(directory, delivery).InviteAsync(room.UserId, room.RoomId,
            new(denied == "empty" ? [] : [target], null)));
        Assert.Equal(code, error.Code);
        Assert.Empty(delivery.Delivered);
        await using var verification = await database.Contexts.CreateDbContextAsync();
        Assert.Equal(1, await verification.ChatRoomMembers.CountAsync(row => row.ChatRoomId == room.RoomId));
        Assert.False(await verification.ChatMessages.AnyAsync(row => row.ChatRoomId == room.RoomId && row.MessageType == "SYSTEM"));
    }

    [Fact]
    public async Task Missing_delivery_rolls_back_rows_that_were_flushed_before_commit()
    {
        // 준비
        var room = await SeedOwnerAsync();
        var service = Service(new MembershipDirectory(room.UserId, room.UserId + 1), null);

        // 실행 / 검증
        await Assert.ThrowsAsync<ChatMembershipDependencyUnavailableException>(() => service.InviteAsync(room.UserId, room.RoomId, new([room.UserId + 1], null)));
        await using var verification = await database.Contexts.CreateDbContextAsync();
        Assert.Equal(1, await verification.ChatRoomMembers.CountAsync(row => row.ChatRoomId == room.RoomId));
        Assert.False(await verification.ChatMessages.AnyAsync(row => row.ChatRoomId == room.RoomId && row.MessageType == "SYSTEM"));
    }

    [Fact]
    public async Task Concurrent_invites_use_real_room_lock_and_create_exactly_one_member_and_system_message()
    {
        // 준비
        var room = await SeedOwnerAsync();
        var directory = new MembershipDirectory(room.UserId, room.UserId + 1);
        var first = Service(directory, new MembershipDelivery(database));
        var second = Service(directory, new MembershipDelivery(database));
        await using var blocker = await database.Contexts.CreateDbContextAsync();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.ChatRooms.FromSqlInterpolated($"SELECT * FROM chat_room WHERE id = {room.RoomId} FOR UPDATE").ToListAsync();
        async Task<string> Invite(ChatMembershipService service)
        {
            try
            {
                await service.InviteAsync(room.UserId, room.RoomId, new([room.UserId + 1], null));
                return "created";
            }
            catch (ChatMembershipException error)
            {
                return error.Code;
            }
        }

        // 실행: 별도 connection의 lock을 두 요청이 실제로 기다리는지 확인한 다음 해제한다.
        var firstAttempt = Invite(first);
        var secondAttempt = Invite(second);
        await using var observer = await database.Contexts.CreateDbContextAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await observer.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM performance_schema.data_lock_waits").SingleAsync(deadline.Token) < 2)
        {
            await Task.Delay(20, deadline.Token);
        }
        Assert.False(firstAttempt.IsCompleted);
        Assert.False(secondAttempt.IsCompleted);
        await transaction.CommitAsync();
        var results = await Task.WhenAll(firstAttempt, secondAttempt);

        // 검증
        Assert.Contains("created", results);
        Assert.Contains("CHAT_ROOM_INVITE_ALREADY_MEMBER", results);
        await using var verification = await database.Contexts.CreateDbContextAsync();
        Assert.Equal(2, await verification.ChatRoomMembers.CountAsync(row => row.ChatRoomId == room.RoomId));
        Assert.Equal(1, await verification.ChatMessages.CountAsync(row => row.ChatRoomId == room.RoomId && row.MessageType == "SYSTEM"));
    }

    [Fact]
    public async Task Friend_GROUP_checks_friend_and_block_ports_and_preserves_untrimmed_name()
    {
        // 준비
        var user = Random.Shared.NextInt64(5_000_000_000, 6_000_000_000);
        var directory = new MembershipDirectory(user, user + 1);
        var delivery = new MembershipDelivery(database);

        // 실행
        var id = await Service(directory, delivery).CreateFriendGroupAsync(user, new(" raw name ", null, [user + 1, user + 1]));

        // 검증
        await using var context = await database.Contexts.CreateDbContextAsync();
        var room = await context.ChatRooms.SingleAsync(row => row.Id == id);
        Assert.Equal("FRIEND", room.SourceType);
        Assert.Equal(" raw name ", room.Name);
        Assert.Equal(2, await context.ChatRoomMembers.CountAsync(row => row.ChatRoomId == id));
        Assert.Empty(delivery.Delivered);
    }

    [Fact]
    public async Task Post_commit_delivery_failure_keeps_saved_membership_and_attempts_later_intents()
    {
        // 준비
        var room = await SeedOwnerAsync();
        var delivery = new FailingMembershipDelivery();
        var service = Service(new MembershipDirectory(room.UserId, room.UserId + 1), delivery);

        // 실행
        var result = await service.InviteAsync(room.UserId, room.RoomId, new([room.UserId + 1], null));

        // 검증: 첫 SYSTEM 전송이 실패해도 commit을 되돌리지 않으며 알림/멤버 변경을 계속 시도한다.
        Assert.Single(result.InvitedMembers);
        Assert.Equal(3, delivery.Attempts);
        await using var context = await database.Contexts.CreateDbContextAsync();
        Assert.True(await context.ChatRoomMembers.AnyAsync(member => member.ChatRoomId == room.RoomId && member.UserId == room.UserId + 1 && member.Active));
        Assert.Equal(1, await context.ChatMessages.CountAsync(message => message.ChatRoomId == room.RoomId && message.MessageType == "SYSTEM"));
    }

    [Fact]
    public async Task Cancellation_before_commit_rolls_back_flushed_member_and_system_without_delivery()
    {
        // 준비
        var room = await SeedOwnerAsync();
        using var cancellation = new CancellationTokenSource();
        var delivery = new CancelBeforeCommitDelivery(cancellation);
        var service = Service(new MembershipDirectory(room.UserId, room.UserId + 1), delivery);

        // 실행 / 검증
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InviteAsync(room.UserId, room.RoomId,
            new([room.UserId + 1], null), cancellation.Token));
        Assert.Equal(0, delivery.Attempts);
        await using var context = await database.Contexts.CreateDbContextAsync();
        Assert.Equal(1, await context.ChatRoomMembers.CountAsync(member => member.ChatRoomId == room.RoomId));
        Assert.False(await context.ChatMessages.AnyAsync(message => message.ChatRoomId == room.RoomId && message.MessageType == "SYSTEM"));
    }

    private ChatMembershipService Service(MembershipDirectory directory, IChatMembershipEventDelivery? delivery)
    {
        return new(new EfChatMembershipStore(database.Contexts, delivery), directory, () => ChatMySqlFixture.Epoch.AddHours(1));
    }

    private async Task<SeededReadRoom> SeedOwnerAsync(string roomType = "GROUP", string source = "MANUAL")
    {
        var room = await database.SeedReadRoomAsync(roomType, Random.Shared.NextInt64(5_000_000_000, 6_000_000_000));
        await using var context = await database.Contexts.CreateDbContextAsync();
        (await context.ChatRooms.SingleAsync(row => row.Id == room.RoomId)).SourceType = source;
        (await context.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId)).Role = "OWNER";
        await context.SaveChangesAsync();
        return room;
    }

    private sealed class MembershipDirectory(params long[] ids) : IChatMembershipDirectory
    {
        public Dictionary<long, ChatMembershipUser> Users
        {
            get;
        } = ids.ToDictionary(id => id,
            id => new ChatMembershipUser(id, $"synthetic-{id}@example.invalid", "합성 계정", "public-" + id, "합성 프로필", null));
        public bool Friends { get; set; } = true;
        public bool Blocked
        {
            get; set;
        }
        public Task<ChatMembershipUser?> FindByIdAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Users.GetValueOrDefault(userId));
        }

        public Task<ChatMembershipUser?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Users.Values.SingleOrDefault(user => user.PublicId == publicId));
        }

        public Task<bool> AreFriendsAsync(long userId, long targetUserId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Friends);
        }

        public Task<bool> IsBlockedBetweenAsync(long userId, long targetUserId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Blocked);
        }
    }

    private sealed class MembershipDelivery(ChatMySqlFixture database) : IChatMembershipEventDelivery
    {
        public List<ChatMembershipIntent> Delivered { get; } = [];
        public bool ObservedCommittedRows
        {
            get; private set;
        }
        public Task ValidateAvailabilityAsync(IReadOnlyList<ChatMembershipIntent> intents, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public async Task DeliverAsync(ChatMembershipIntent intent, CancellationToken cancellationToken)
        {
            // 별도 connection에서 실제 저장 행을 볼 수 있어야 commit 후 전달임을 확인할 수 있다.
            await using var context = await database.Contexts.CreateDbContextAsync(cancellationToken);
            if (intent is ChatMembershipInvitationCommitted invitation)
            {
                Assert.True(await context.ChatRoomMembers.AnyAsync(member => member.Id == invitation.MemberId && member.Active, cancellationToken));
                ObservedCommittedRows = true;
            }
            Delivered.Add(intent);
        }
    }

    private sealed class FailingMembershipDelivery : IChatMembershipEventDelivery
    {
        public int Attempts
        {
            get; private set;
        }
        public Task ValidateAvailabilityAsync(IReadOnlyList<ChatMembershipIntent> intents, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task DeliverAsync(ChatMembershipIntent intent, CancellationToken cancellationToken)
        {
            Attempts++;
            return intent is ChatMembershipMessageCreated
                ? Task.FromException(new IOException("Synthetic transport failure.")) : Task.CompletedTask;
        }
    }

    private sealed class CancelBeforeCommitDelivery(CancellationTokenSource cancellation) : IChatMembershipEventDelivery
    {
        public int Attempts
        {
            get; private set;
        }
        public Task ValidateAvailabilityAsync(IReadOnlyList<ChatMembershipIntent> intents, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public Task DeliverAsync(ChatMembershipIntent intent, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.CompletedTask;
        }
    }
}
