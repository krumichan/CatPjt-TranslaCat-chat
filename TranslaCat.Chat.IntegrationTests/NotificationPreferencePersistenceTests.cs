using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Infrastructure.Persistence.Notifications;
using TranslaCat.Chat.Infrastructure.Push;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class NotificationPreferencePersistenceTests(ChatMySqlFixture fixture)
{
    private static long nextUser = Random.Shared.NextInt64(2_000_000_000, 9_000_000_000);
    private static long User() => Interlocked.Increment(ref nextUser);
    private EfChatNotificationPreferenceStore Preferences() => new(fixture.Contexts);

    [Fact]
    public async Task Defaults_and_personal_settings_preserve_unread_cursor_and_activity()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync(userId: User());
        var store = Preferences();
        var unread = new EfChatNotificationStore(fixture.Contexts);
        var before = await unread.GetSummaryAsync(room.UserId, default);
        Assert.True((await store.GetAsync(room.UserId, default)).PushEnabled);
        Assert.False((await store.GetRoomAsync(room.UserId, room.RoomId, default))!.Muted);

        // 실행
        await store.PutAsync(room.UserId, false, default);
        await store.PutRoomAsync(room.UserId, room.RoomId, true, default);
        var after = await unread.GetSummaryAsync(room.UserId, default);

        // 검증: 자기 수신 정책만 바뀌며 방 참여/안읽음/다른 사용자 설정은 보존한다.
        Assert.Equal(before, after);
        Assert.False((await store.GetAsync(room.UserId, default)).PushEnabled);
        Assert.True((await store.GetRoomAsync(room.UserId, room.RoomId, default))!.Muted);
        Assert.True((await store.GetAsync(room.UserId + 1000, default)).PushEnabled);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        var member = await db.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId);
        Assert.True(member.Active);
        Assert.Null(member.LastReadMessageId);
        Assert.Null(member.LastReadAt);
    }

    [Fact]
    public async Task Global_off_blocks_message_then_reenable_discards_old_backlog_without_changing_same_value_cutoff()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync(userId: User());
        var preferences = Preferences();
        var store = new EfChatPushStore(fixture.Contexts, new());
        var job = Job(room);
        Assert.True(await store.EligibleAsync(job, default));

        // 실행 / 검증: 전역 off와 다시 켜기 전 대기 메시지는 발송 대상이 아니다.
        await preferences.PutAsync(room.UserId, false, default);
        Assert.False(await store.EligibleAsync(job, default));
        await preferences.PutAsync(room.UserId, true, default);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        var row = await db.ChatNotificationPreferences.AsNoTracking().SingleAsync(value => value.UserId == room.UserId);
        Assert.NotNull(row.EnabledSinceUtc);
        job.CreatedAtUtc = row.EnabledSinceUtc!.Value.AddTicks(-10);
        Assert.False(await store.EligibleAsync(job, default));
        job.CreatedAtUtc = row.EnabledSinceUtc.Value.AddTicks(10);
        Assert.True(await store.EligibleAsync(job, default));

        // 실행 / 검증: 불확실한 성공의 같은 값 재전송은 cutoff를 바꾸지 않는다.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => preferences.PutAsync(room.UserId, true, default)));
        var replay = await db.ChatNotificationPreferences.AsNoTracking().SingleAsync(value => value.UserId == room.UserId);
        Assert.Equal(row.EnabledSinceUtc, replay.EnabledSinceUtc);
        Assert.Equal(1, await db.ChatNotificationPreferences.CountAsync(value => value.UserId == room.UserId));
    }

    [Fact]
    public async Task Room_mute_is_personal_and_unmute_drops_only_own_old_backlog()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync(userId: User());
        long other = User() + 1000;
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        db.ChatRoomMembers.Add(new() { ChatRoomId = room.RoomId, UserId = other, Role = "MEMBER",
            JoinedAt = ChatMySqlFixture.Epoch, CreatedAt = ChatMySqlFixture.Epoch, UpdatedAt = ChatMySqlFixture.Epoch });
        await db.SaveChangesAsync();
        var preferences = Preferences();
        var push = new EfChatPushStore(fixture.Contexts, new());
        var own = Job(room);
        var otherJob = Job(room); otherJob.RecipientUserId = other;

        // 실행 / 검증
        await preferences.PutRoomAsync(room.UserId, room.RoomId, true, default);
        Assert.False(await push.EligibleAsync(own, default));
        Assert.True(await push.EligibleAsync(otherJob, default));
        await preferences.PutRoomAsync(room.UserId, room.RoomId, false, default);
        var member = await db.ChatRoomMembers.AsNoTracking().SingleAsync(row => row.Id == room.MemberId);
        Assert.NotNull(member.NotificationEnabledSinceUtc);
        own.CreatedAtUtc = member.NotificationEnabledSinceUtc!.Value.AddTicks(-10);
        Assert.False(await push.EligibleAsync(own, default));
        own.CreatedAtUtc = member.NotificationEnabledSinceUtc.Value.AddTicks(10);
        Assert.True(await push.EligibleAsync(own, default));
        await preferences.PutRoomAsync(room.UserId, room.RoomId, false, default);
        var same = await db.ChatRoomMembers.AsNoTracking().SingleAsync(row => row.Id == room.MemberId);
        Assert.Equal(member.NotificationEnabledSinceUtc, same.NotificationEnabledSinceUtc);
        Assert.Null(same.LastReadMessageId);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("inactive-member")]
    [InlineData("deleted-member")]
    [InlineData("inactive-room")]
    [InlineData("deleted-room")]
    [InlineData("closed-open")]
    [InlineData("banned")]
    public async Task Only_current_authorized_member_can_read_or_change_own_room_preference(string scenario)
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync("OPEN", User());
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        var member = await db.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId);
        var entity = await db.ChatRooms.SingleAsync(row => row.Id == room.RoomId);
        var open = new OpenChatRoomEntity { ChatRoomId = room.RoomId, Visibility = "PUBLIC", MaxMemberCount = 10,
            Status = scenario == "closed-open" ? "CLOSED" : "ACTIVE", CreatedAt = ChatMySqlFixture.Epoch, UpdatedAt = ChatMySqlFixture.Epoch };
        db.OpenChatRooms.Add(open);
        if (scenario == "inactive-member") member.Active = false;
        if (scenario == "deleted-member") member.DeletedAt = ChatMySqlFixture.Epoch;
        if (scenario == "inactive-room") entity.Active = false;
        if (scenario == "deleted-room") entity.DeletedAt = ChatMySqlFixture.Epoch;
        if (scenario == "banned") db.OpenChatBans.Add(new()
        {
            ChatRoomId = room.RoomId, TargetUserId = room.UserId, TargetChatRoomMemberId = room.MemberId,
            TargetMemberCode = "OC-SYNTH", NicknameSnapshot = "synthetic", LastJoinedAtSnapshot = member.JoinedAt,
            TargetRoleSnapshot = "MEMBER", BannedByMemberId = room.MemberId, BannedByRole = "OWNER",
            BannedAt = ChatMySqlFixture.Epoch, Reason = "synthetic", CreatedAt = ChatMySqlFixture.Epoch, UpdatedAt = ChatMySqlFixture.Epoch
        });
        await db.SaveChangesAsync();
        long actor = scenario == "foreign" ? User() : room.UserId;

        // 실행 / 검증
        var store = Preferences();
        Assert.Null(await store.GetRoomAsync(actor, room.RoomId, default));
        Assert.Null(await store.PutRoomAsync(actor, room.RoomId, true, default));
        Assert.False((await db.ChatRoomMembers.AsNoTracking().SingleAsync(row => row.Id == room.MemberId)).NotificationMuted);
    }

    [Fact]
    public async Task Open_invitation_obeys_global_off_and_resume_cutoff_but_inbox_remains_unread()
    {
        // 준비
        long recipient = User();
        var room = await fixture.SeedReadRoomAsync("OPEN", User());
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        db.OpenChatRooms.Add(new() { ChatRoomId = room.RoomId, Visibility = "PUBLIC", MaxMemberCount = 10, Status = "ACTIVE",
            CreatedAt = ChatMySqlFixture.Epoch, UpdatedAt = ChatMySqlFixture.Epoch });
        var notification = new ChatNotificationEntity
        {
            RecipientUserId = recipient, ActorUserId = room.UserId, ChatRoomId = room.RoomId, NotificationType = "CHAT_INVITATION",
            PayloadJson = "{\"schemaVersion\":1,\"invitationKind\":\"OPEN_LINK\",\"roomType\":\"OPEN\"}",
            SourceEventKey = "open-link:synthetic:" + Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow
        };
        db.ChatNotifications.Add(notification);
        await db.SaveChangesAsync();
        var job = new OpenInvitationPushOutboxEntity { NotificationId = notification.Id, RoomId = room.RoomId,
            SenderUserId = room.UserId, RecipientUserId = recipient, CreatedAtUtc = DateTime.UtcNow,
            EventId = Guid.NewGuid().ToString("D"), DueAtUtc = DateTime.UtcNow };
        db.OpenInvitationPushOutbox.Add(job);
        await db.SaveChangesAsync();
        var push = new EfOpenInvitationPushStore(fixture.Contexts, new());
        var preferences = Preferences();
        Assert.True(await push.EligibleAsync(job, default));

        // 실행 / 검증
        await preferences.PutAsync(recipient, false, default);
        Assert.False(await push.EligibleAsync(job, default));
        await preferences.PutAsync(recipient, true, default);
        var settings = await db.ChatNotificationPreferences.AsNoTracking().SingleAsync(row => row.UserId == recipient);
        job.CreatedAtUtc = settings.EnabledSinceUtc!.Value.AddTicks(-10);
        Assert.False(await push.EligibleAsync(job, default));
        job.CreatedAtUtc = settings.EnabledSinceUtc.Value.AddTicks(10);
        Assert.True(await push.EligibleAsync(job, default));
        var item = await db.ChatNotifications.AsNoTracking().SingleAsync(row => row.Id == notification.Id);
        Assert.False(item.IsRead);
        Assert.Null(item.ReadAt);
        Assert.Equal(1, (await new EfChatNotificationStore(fixture.Contexts).GetSummaryAsync(recipient, default)).UnreadActivityCount);
    }

    private static ChatPushOutboxEntity Job(SeededReadRoom room) => new()
    {
        RoomId = room.RoomId, MessageId = room.FirstId, SenderUserId = room.UserId + 1,
        RecipientUserId = room.UserId, CreatedAtUtc = DateTime.UtcNow
    };
}
