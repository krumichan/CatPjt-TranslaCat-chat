using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Runtime;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.Language;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.NotificationPreferences;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Application.OpenInvitations;
using TranslaCat.Chat.Application.OpenMembership;
using TranslaCat.Chat.Application.OpenModeration;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.Read;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Application.Safety;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Infrastructure.Safety;

namespace TranslaCat.Chat.IntegrationTests;

public sealed partial class SafetyPersistenceTests
{
    [Fact]
    public async Task Account_gate_proof_does_not_leak_into_next_transaction_on_reused_context()
    {
        // 준비: pooling이 재사용하는 같은 context의 사용자 필드를 첫 transaction에 채운다.
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        long account = Random.Shared.NextInt64(1_000_000_000, 2_000_000_000);
        await using (var first = await db.Database.BeginTransactionAsync())
        {
            await ChatAccountWriteGate.AcquireAsync(db, [account], default);
            ChatAccountWriteGate.RequireHeld(db, account);
            await first.CommitAsync();
        }

        // 실행 / 검증: 다음 transaction은 과거 user set이 아니라 자신의 실제 gate만 증명한다.
        await using var second = await db.Database.BeginTransactionAsync();
        Assert.Throws<ChatSafetyException>(() => ChatAccountWriteGate.RequireHeld(db, account));
        await ChatAccountWriteGate.AcquireAsync(db, [account + 1], default);
        ChatAccountWriteGate.RequireHeld(db, account + 1);
        Assert.Throws<ChatSafetyException>(() => ChatAccountWriteGate.RequireHeld(db, account));
        await second.CommitAsync();
    }

    [Theory]
    [InlineData("join")]
    [InlineData("leave")]
    [InlineData("transfer")]
    [InlineData("close")]
    [InlineData("profile")]
    [InlineData("group-invite")]
    [InlineData("friend-direct")]
    [InlineData("friend-group")]
    [InlineData("convert")]
    [InlineData("open-invite")]
    [InlineData("open-invite-target")]
    [InlineData("group-invite-target")]
    [InlineData("default-language")]
    [InlineData("room-language")]
    [InlineData("global-notification")]
    [InlineData("room-notification")]
    [InlineData("activity")]
    [InlineData("ai-create")]
    [InlineData("ai-settings")]
    [InlineData("ai-system")]
    [InlineData("read")]
    [InlineData("moderation-admin")]
    [InlineData("moderation-ban")]
    [InlineData("report")]
    public async Task Actual_mutation_and_cleanup_share_account_fence_and_reject_late_reinsertion(string scenario)
    {
        // 준비: production DI factory와 실제 store를 사용하고 계정/전달 외부 port만 합성으로 연결한다.
        bool open = scenario is "join" or "leave" or "transfer" or "close" or "profile" or "open-invite"
            or "open-invite-target" or "moderation-admin" or "moderation-ban";
        var seed = await SeedAsync(50001, open ? "OPEN" : "GROUP");
        if (scenario == "convert")
        {
            await using var setup = await fixture.Contexts.CreateDbContextAsync();
            var room = await setup.ChatRooms.SingleAsync(row => row.Id == seed.RoomId);
            room.RoomType = "DIRECT";
            room.SourceType = "FRIEND";
            await setup.SaveChangesAsync();
        }
        long actor = scenario == "join" ? seed.UserId + 2 : scenario == "leave" ? seed.UserId + 1 : seed.UserId;
        long deleted = scenario.EndsWith("-target", StringComparison.Ordinal) ? seed.UserId + 2 : actor;
        using var provider = FenceServices(enabled: true);
        using var scope = provider.CreateScope();
        Func<Task> mutation = Mutation(scope.ServiceProvider, seed, actor, scenario);
        await using var blocker = await fixture.Contexts.CreateDbContextAsync();
        await using var gate = await blocker.Database.BeginTransactionAsync();
        await ChatAccountWriteGate.AcquireAsync(blocker, [deleted], default);

        // 실행: 두 실제 요청을 gate 뒤에서 경쟁시킨다. 먼저 시작한 요청도 사전 조회 중이면 cleanup이 먼저 잠글 수 있다.
        Task oldWrite = mutation();
        await Task.Delay(150);
        Assert.False(oldWrite.IsCompleted);
        var cleanup = new EfChatAccountDeletionStore(fixture.Contexts, null, instant => instant.UtcDateTime,
            new FixedTime { Instant = new DateTimeOffset(Now.AddMinutes(1), TimeSpan.Zero) });
        var operation = new ChatAccountDeletionRequest(Guid.NewGuid().ToString("D"), new DateTimeOffset(Now, TimeSpan.Zero), Audit(deleted));
        Task<ChatAccountDeletionResult> deletion = cleanup.DeleteAsync(deleted, operation, default);
        await Task.Delay(100);
        Assert.False(deletion.IsCompleted);
        await gate.CommitAsync();
        var racedFailure = await Record.ExceptionAsync(async () => await oldWrite.WaitAsync(TimeSpan.FromSeconds(15)));
        // 허용하는 두 직렬 결과는 이전 쓰기 commit→정리, 또는 정리 commit→쓰기의 명시적 거절뿐이다.
        Assert.True(racedFailure is null or ChatSafetyException { Code: "CHAT_ACCOUNT_DELETED" }, racedFailure?.ToString());
        Assert.Equal("COMPLETED", (await deletion.WaitAsync(TimeSpan.FromSeconds(15))).Status);

        // 검증: 같은 실제 경로의 늦은 호출은 업무 쓰기 전에 거절되며 private 상태는 다시 생기지 않는다.
        Exception? failure = await Record.ExceptionAsync(mutation);
        Assert.NotNull(failure);
        Assert.True(failure is ChatSafetyException { Code: "CHAT_ACCOUNT_DELETED" }
            || failure is ChatReadException { Code: "OPEN_CHAT_MEMBER_ACCESS_DENIED" }, failure.ToString());
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        Assert.False(await read.ChatRoomMembers.AnyAsync(row => row.UserId == deleted && row.Active));
        Assert.False(await read.UserChatLanguageSettings.AnyAsync(row => row.UserId == deleted));
        Assert.False(await read.ChatNotificationPreferences.AnyAsync(row => row.UserId == deleted));
        Assert.False(await read.ChatRooms.AnyAsync(row => row.CreatedBy == Audit(deleted) || row.UpdatedBy == Audit(deleted)));
        Assert.False(await read.ChatMessageTranslations.AnyAsync(row => row.CreatedBy == Audit(deleted) || row.UpdatedBy == Audit(deleted)));
        Assert.False(await read.OpenChatMemberProfiles.AnyAsync(row => read.ChatRoomMembers.Any(member => member.Id == row.ChatRoomMemberId
            && member.UserId == deleted) && (row.Nickname != "탈퇴한 사용자" || row.ProfileImageObjectKey != null)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Production_factories_resolve_with_safety_disabled_and_enabled(bool enabled)
    {
        // 준비 / 실행: default false와 명시 true 모두 동일 production 등록을 거친다.
        using var provider = FenceServices(enabled);
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        // 검증: optional policy만 달라지고 기존 mutation factory가 모두 생성된다.
        Assert.Equal(enabled, services.GetService<IChatContentPolicy>() is not null);
        Assert.Equal(enabled, services.GetService<IOpenProfileUploadJournal>() is not null);
        Assert.NotNull(services.GetRequiredService<IChatMembershipStore>());
        Assert.NotNull(services.GetRequiredService<IOpenMembershipStore>());
        Assert.NotNull(services.GetRequiredService<IOpenInvitationStore>());
        Assert.NotNull(services.GetRequiredService<IOpenModerationStore>());
        Assert.NotNull(services.GetRequiredService<IChatLanguageStore>());
        Assert.NotNull(services.GetRequiredService<IChatNotificationPreferenceStore>());
        Assert.NotNull(services.GetRequiredService<IChatActivityNotificationWriter>());
        Assert.NotNull(services.GetRequiredService<IChatAiManagementStore>());
        Assert.NotNull(services.GetRequiredService<IChatReadTransaction>());
        Assert.Equal(enabled, services.GetService<IChatAccountDeletionStore>() is not null);
    }

    private ServiceProvider FenceServices(bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Chat:SourceTimeZone"] = "UTC", ["Chat:Safety:Enabled"] = enabled.ToString(),
            ["Chat:Safety:AccountDeletionEnabled"] = enabled.ToString()
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddChatReadHttp();
        services.AddChatRuntime(configuration);
        services.AddSingleton<TimeProvider>(new FixedTime { Instant = new DateTimeOffset(Now.AddSeconds(30), TimeSpan.Zero) });
        services.AddChatDatabase(fixture.ConnectionString, isolatedTestCatalog: true);
        services.AddSingleton(Core(new CoreHandler(false)));
        var ports = new FencePorts();
        services.AddSingleton<IChatRoomAccountReader>(ports);
        services.AddSingleton<IChatMessageProfileReader>(ports);
        services.AddSingleton<IChatMembershipDirectory>(ports);
        services.AddSingleton<IChatMembershipEventDelivery>(ports);
        services.AddSingleton<IOpenMembershipDelivery>(ports);
        services.AddSingleton<IOpenBanEventDelivery>(ports);
        services.AddSingleton<IOpenProfileEventDelivery>(ports);
        services.AddSingleton<IOpenProfileStorage>(ports);
        services.AddSingleton<IChatAiProfileStorage>(ports);
        services.AddSingleton<IChatActivityNotificationDelivery>(ports);
        services.AddSingleton<IChatReadRecipientResolver>(ports);
        services.AddSingleton<IChatReadEventDelivery>(ports);
        return services.BuildServiceProvider();
    }

    private static Func<Task> Mutation(IServiceProvider services, Seed seed, long actor, string scenario)
    {
        long target = seed.UserId + 2;
        var members = services.GetRequiredService<IOpenMembershipStore>();
        var now = Now.AddSeconds(30);
        return scenario switch
        {
            "join" => () => members.JoinAsync(actor, seed.RoomId, new("늦은 합성 참여자", null), now, default),
            "leave" => () => members.LeaveAsync(actor, seed.RoomId, now, default),
            "transfer" => () => members.TransferAsync(actor, seed.RoomId, seed.OtherMemberId, now, default),
            "close" => () => members.CloseAsync(actor, seed.RoomId, now, default),
            "profile" => () => services.GetRequiredService<IOpenRoomStore>().UpdateNicknameAsync(actor, seed.RoomId, "늦은 프로필", now, default),
            "group-invite" or "group-invite-target" => () => services.GetRequiredService<ChatMembershipService>().InviteAsync(actor, seed.RoomId, new([target], null)),
            "friend-direct" => () => services.GetRequiredService<ChatMembershipService>().CreateOrGetFriendDirectAsync(actor, target),
            "friend-group" => () => services.GetRequiredService<ChatMembershipService>().CreateFriendGroupAsync(actor, new("늦은 그룹", null, [target])),
            "convert" => () => services.GetRequiredService<ChatMembershipService>().ConvertAsync(actor, seed.RoomId, new("전환된 그룹", null, new([target], null))),
            "open-invite" or "open-invite-target" => () => services.GetRequiredService<IOpenInvitationStore>().CreateAsync(actor, seed.RoomId, new(PublicId(target), Guid.NewGuid()), default),
            "default-language" => () => services.GetRequiredService<ChatLanguageService>().UpdateDefaultAsync(actor, new("ko", "ja", true, true)),
            "room-language" => () => services.GetRequiredService<ChatLanguageService>().UpdateRoomAsync(actor, seed.RoomId, new("ko", "ja", true, true)),
            "global-notification" => () => services.GetRequiredService<IChatNotificationPreferenceStore>().PutAsync(actor, false, default),
            "room-notification" => () => services.GetRequiredService<IChatNotificationPreferenceStore>().PutRoomAsync(actor, seed.RoomId, true, default),
            "activity" => () => services.GetRequiredService<IChatActivityNotificationWriter>().CreateAsync(new(actor, Audit(actor), "CHAT_INVITATION", seed.RoomId,
                seed.UserId + 1, "{\"roomName\":\"합성\"}", "fence:" + Guid.NewGuid().ToString("N"), Audit(seed.UserId + 1)), default),
            "ai-create" => () => services.GetRequiredService<IChatAiManagementStore>().CreateAsync(actor, seed.RoomId, new("합성 AI", null, "ja", "합성 persona"), now, default),
            "ai-settings" => () => services.GetRequiredService<IChatAiManagementStore>().RoomSettingsAsync(actor, seed.RoomId, new(null, null, false, false), now, default),
            "ai-system" => () => services.GetRequiredService<IChatAiManagementStore>().SystemSettingsAsync(actor, null, now, default),
            "read" => () => new ChatRoomReadService(services.GetRequiredService<IChatReadTransaction>(), () => now).MarkAsReadAsync(actor, seed.RoomId, new(seed.MessageId)),
            "moderation-admin" => () => services.GetRequiredService<IOpenModerationStore>().ChangeAdminAsync(actor, seed.RoomId, seed.OtherMemberId, true, now, default),
            "moderation-ban" => () => services.GetRequiredService<IOpenModerationStore>().BanAsync(actor, seed.RoomId, seed.OtherMemberId, "합성 운영 이유", now, default),
            "report" => () => services.GetRequiredService<IChatReportStore>().CreateAsync(actor, seed.RoomId, new("MESSAGE", seed.MessageId, null, null, "SPAM", null, Guid.NewGuid().ToString("D")), now, default),
            _ => throw new InvalidOperationException("Unknown synthetic mutation.")
        };
    }

    private static string Audit(long userId) => $"fence-{userId}@example.invalid";
    private static string PublicId(long userId) => "FENCE-" + userId;

    private sealed class FencePorts : IChatRoomAccountReader, IChatMessageProfileReader, IChatMembershipDirectory,
        IChatMembershipEventDelivery, IOpenMembershipDelivery, IOpenBanEventDelivery, IOpenProfileEventDelivery,
        IOpenProfileStorage, IChatAiProfileStorage, IChatActivityNotificationDelivery, IChatReadRecipientResolver, IChatReadEventDelivery
    {
        public bool IsConfigured => true;
        public Task EnsureUserExistsAsync(long? userId, CancellationToken token) => Task.CompletedTask;
        public Task<string> GetAuditIdentityAsync(long userId, CancellationToken token) => Task.FromResult(Audit(userId));
        public Task<ChatDirectPartner> GetDirectPartnerAsync(long userId, CancellationToken token) => Task.FromResult(new ChatDirectPartner(userId, PublicId(userId), "합성", null, null, null, null));
        public Task<ChatUserMessageProfile> GetUserAsync(long userId, CancellationToken token) => Task.FromResult(new ChatUserMessageProfile(userId, "합성", Audit(userId), null));
        public Task<string?> ResolveObjectUrlAsync(string key, CancellationToken token) => Task.FromResult<string?>(null);
        public Task<string?> ResolveUrlAsync(string key, CancellationToken token) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string key, CancellationToken token) => Task.CompletedTask;
        public Task<ChatMembershipUser?> FindByIdAsync(long id, CancellationToken token) => Task.FromResult<ChatMembershipUser?>(new(id, Audit(id), "합성", PublicId(id), "합성", null));
        public Task<ChatMembershipUser?> FindByPublicIdAsync(string value, CancellationToken token) => FindByIdAsync(long.Parse(value[6..]), token);
        public Task<bool> AreFriendsAsync(long userId, long target, CancellationToken token) => Task.FromResult(true);
        public Task<bool> IsBlockedBetweenAsync(long userId, long target, CancellationToken token) => Task.FromResult(false);
        public Task ValidateAvailabilityAsync(IReadOnlyList<ChatMembershipIntent> intents, CancellationToken token) => Task.CompletedTask;
        public Task DeliverAsync(ChatMembershipIntent intent, CancellationToken token) => Task.CompletedTask;
        public Task ValidateAvailabilityAsync(IReadOnlyList<OpenMembershipIntent> intents, CancellationToken token) => Task.CompletedTask;
        public Task DeliverAsync(OpenMembershipIntent intent, CancellationToken token) => Task.CompletedTask;
        public Task ValidateAvailabilityAsync(CancellationToken token) => Task.CompletedTask;
        public Task DeliverAsync(OpenMemberBanned change, CancellationToken token) => Task.CompletedTask;
        public Task DeliverAsync(OpenProfileChanged change, CancellationToken token) => Task.CompletedTask;
        public Task DeliverAsync(string recipient, ChatNotificationActivity notification, DateTime now, CancellationToken token) => Task.CompletedTask;
        public Task<string?> ResolveUsernameAsync(long userId, CancellationToken token) => Task.FromResult<string?>(Audit(userId));
        public Task DeliverAsync(ChatReadUpdated intent, CancellationToken token) => Task.CompletedTask;
        public Task DeliverAsync(ChatMemberReadUpdated intent, CancellationToken token) => Task.CompletedTask;
    }
}
