using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class OpenMembershipRuntimeTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task JoinLeaveRestore_PreservesProfileAndResetsReadBoundaryToLatestMessage()
    {
        // 준비 — 계정만 합성 대역이며 HTTP/EF/Redis는 실제 adapter를 사용한다.
        long owner = UserId();
        long user = owner + 1;
        await using var host = await HostAsync();
        long room = await CreateAsync(host, owner);
        var first = await SuccessAsync(host, user, HttpMethod.Post, Path(room, "join"), "{\"profile\":{\"nickname\":\"  합성 참여자  \"}}");
        long memberId = first.GetProperty("myOpenProfile").GetProperty("openChatMemberId").GetInt64();
        string? code = first.GetProperty("myOpenProfile").GetProperty("memberCode").GetString();

        // 실행 — 활성 중 중복 요청은 nickname을 바꾸지 않으며 나가기 뒤 재참여는 기존 profile을 재사용한다.
        var noOp = await SuccessAsync(host, user, HttpMethod.Post, Path(room, "join"), "{\"profile\":{\"nickname\":\"\"}}");
        var left = await SuccessAsync(host, user, HttpMethod.Delete, Path(room, "leave"));
        long messageId;
        await using (var db = await fixture.Contexts.CreateDbContextAsync())
        {
            var message = new ChatMessageEntity
            {
                ChatRoomId = room,
                SenderType = "SYSTEM",
                Content = "합성 시스템 메시지",
                Status = "SENT",
                MessageType = "SYSTEM",
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            };
            db.ChatMessages.Add(message);
            db.UserChatLanguageSettings.Add(new UserChatLanguageSettingEntity
            {
                UserId = user,
                OriginalLanguageCode = "en",
                TranslationLanguageCode = "ja",
                ShowOriginal = false,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            await db.SaveChangesAsync();
            messageId = message.Id;
        }
        var restored = await SuccessAsync(host, user, HttpMethod.Post, Path(room, "join"));

        // 검증 — 처음 null cursor/읽음 시각을 만들고 복귀 때 최신 SENT(system 포함)으로 초기화한다.
        Assert.Equal("합성 참여자", noOp.GetProperty("myOpenProfile").GetProperty("nickname").GetString());
        Assert.False(left.GetProperty("joined").GetBoolean());
        Assert.False(left.GetProperty("myOpenProfile").GetProperty("active").GetBoolean());
        Assert.Equal(code, restored.GetProperty("myOpenProfile").GetProperty("memberCode").GetString());
        Assert.Equal(memberId, restored.GetProperty("myOpenProfile").GetProperty("openChatMemberId").GetInt64());
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        var member = await read.ChatRoomMembers.SingleAsync(row => row.Id == memberId);
        Assert.True(member.Active);
        Assert.Null(member.LeftAt);
        Assert.Equal(messageId, member.LastReadMessageId);
        Assert.NotNull(member.LastReadAt);
        Assert.Equal("en", member.OriginalLanguageCode);
        Assert.False(member.ShowOriginal);
    }

    [Fact]
    public async Task TransferThenClose_CommitsRolesAndNotificationsAndDeliversClosureAcrossApps()
    {
        // 준비
        long owner = UserId();
        long nextOwner = owner + 1;
        string prefix = Prefix();
        await using var first = await HostAsync(prefix);
        await using var second = await HostAsync(prefix);
        long room = await CreateAsync(first, owner);
        var joined = await SuccessAsync(first, nextOwner, HttpMethod.Post, Path(room, "join"), "{\"profile\":{\"nickname\":\"다음 소유자\"}}");
        long memberId = joined.GetProperty("myOpenProfile").GetProperty("openChatMemberId").GetInt64();
        using var socket = await second.ConnectAsync(owner);
        await socket.SubscribeAsync("room", $"/topic/chat/rooms/{room}");

        // 실행 — source commit 뒤 실 Redis를 경유해 다른 app의 구독으로 전달한다.
        var transfer = await SuccessAsync(first, owner, HttpMethod.Post, Path(room, "owner-transfer"), $"{{\"targetOpenChatMemberId\":\"{memberId}\"}}");
        var closed = await SuccessAsync(first, nextOwner, HttpMethod.Post, Path(room, "close"));
        var closeFrame = await socket.ReceiveEventAsync("chat.room.closed");
        await SuccessAsync(first, nextOwner, HttpMethod.Post, Path(room, "close"));

        // 검증 — 반복 종료는 알림을 추가하지 않는다. 원본처럼 멤버/메시지는 보존한다.
        Assert.Equal("MEMBER", transfer.GetProperty("myRole").GetString());
        Assert.Equal("CLOSED", closed.GetProperty("status").GetString());
        Assert.Equal(room, closeFrame.GetProperty("roomId").GetInt64());
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(nextOwner, (await db.ChatRooms.SingleAsync(row => row.Id == room)).OwnerId);
        Assert.Equal(2, await db.ChatRoomMembers.CountAsync(row => row.ChatRoomId == room && row.Active));
        Assert.Equal(1, await db.ChatNotifications.CountAsync(row => row.ChatRoomId == room && row.NotificationType == "OPEN_CHAT_ROLE_CHANGED"));
        Assert.Equal(1, await db.ChatNotifications.CountAsync(row => row.ChatRoomId == room && row.NotificationType == "OPEN_CHAT_ROOM_CLOSED"));
        await ErrorAsync(first, owner + 2, HttpMethod.Post, Path(room, "join"), "OPEN_CHAT_ROOM_CLOSED", "{\"profile\":{\"nickname\":\"거절\"}}");
    }

    [Fact]
    public async Task OwnerLeaveAndInvalidTransfer_PreserveMembership()
    {
        // 준비
        long owner = UserId();
        await using var host = await HostAsync();
        long room = await CreateAsync(host, owner);

        // 실행 / 검증 — 원본 권한/입력 검사는 상태 갱신 전에 실행한다.
        await ErrorAsync(host, owner, HttpMethod.Delete, Path(room, "leave"), "OPEN_CHAT_OWNER_CLOSE_REQUIRED");
        await ErrorAsync(host, owner, HttpMethod.Post, Path(room, "owner-transfer"), "OPEN_CHAT_OWNER_TRANSFER_TARGET_REQUIRED", "{}");
        await SuccessAsync(host, owner + 1, HttpMethod.Post, Path(room, "join"), "{\"profile\":{\"nickname\":\"멤버\"}}");
        await ErrorAsync(host, owner, HttpMethod.Delete, Path(room, "leave"), "OPEN_CHAT_OWNER_TRANSFER_REQUIRED");
        await ErrorAsync(host, owner + 1, HttpMethod.Post, Path(room, "close"), "OPEN_CHAT_OWNER_ONLY");
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(2, await db.ChatRoomMembers.CountAsync(row => row.ChatRoomId == room && row.Active));
        Assert.Equal(owner, (await db.ChatRooms.SingleAsync(row => row.Id == room)).OwnerId);
    }

    [Fact]
    public async Task ConcurrentJoin_UsesActualDatabaseLockAndDoesNotExceedCapacity()
    {
        // 준비 — 별도 connection으로 OPEN row를 잠가 두 요청이 실제 DB에서 대기하도록 만든다.
        long owner = UserId();
        await using var host = await HostAsync();
        long room = await CreateAsync(host, owner, 2);
        await using var blocker = await fixture.Contexts.CreateDbContextAsync();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.OpenChatRooms.FromSqlInterpolated($"SELECT * FROM open_chat_room WHERE chat_room_id = {room} FOR UPDATE").ToListAsync();
        using var firstRequest = Request(host, owner + 1, HttpMethod.Post, Path(room, "join"), "{\"profile\":{\"nickname\":\"첫째\"}}");
        using var secondRequest = Request(host, owner + 2, HttpMethod.Post, Path(room, "join"), "{\"profile\":{\"nickname\":\"둘째\"}}");

        // 실행 — process lock이나 같은 DbContext가 아닌 두 HTTP scope/context가 경합한다.
        var first = host.Client.SendAsync(firstRequest);
        var second = host.Client.SendAsync(secondRequest);
        await using var observer = await fixture.Contexts.CreateDbContextAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await observer.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM performance_schema.data_lock_waits").SingleAsync(deadline.Token) < 2)
        {
            await Task.Delay(20, deadline.Token);
        }
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        await transaction.CommitAsync();
        var responses = await Task.WhenAll(first, second);

        // 검증
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal(2, await observer.ChatRoomMembers.CountAsync(row => row.ChatRoomId == room && row.Active));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task ProjectionFailure_RollsBackNewMembershipAndDoesNotPublishChanges()
    {
        // 준비 — URL adapter가 필요한 기존 owner key를 합성 row에만 설정한다.
        long owner = UserId();
        await using var host = await HostAsync();
        long room = await CreateAsync(host, owner);
        await using (var db = await fixture.Contexts.CreateDbContextAsync())
        {
            long memberId = await db.ChatRoomMembers.Where(row => row.ChatRoomId == room).Select(row => row.Id).SingleAsync();
            await db.OpenChatMemberProfiles.Where(row => row.ChatRoomMemberId == memberId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ProfileImageObjectKey, $"open-chat-profiles/{memberId}/synthetic.png"));
        }

        // 실행 — insert 뒤 response projection이 실패해도 commit되지 않아야 한다.
        using var request = Request(host, owner + 1, HttpMethod.Post, Path(room, "join"), "{\"profile\":{\"nickname\":\"롤백\"}}");
        using var response = await host.Client.SendAsync(request);

        // 검증
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(1, await read.ChatRoomMembers.CountAsync(row => row.ChatRoomId == room));
        Assert.False(await read.ChatRoomMembers.AnyAsync(row => row.ChatRoomId == room && row.UserId == owner + 1));
    }

    [Theory]
    [InlineData(null, "OPEN_CHAT_JOIN_PROFILE_REQUIRED")]
    [InlineData("{}", "OPEN_CHAT_JOIN_PROFILE_REQUIRED")]
    [InlineData("{\"profile\":{\"nickname\":\"  \"}}", "OPEN_CHAT_NICKNAME_REQUIRED")]
    public async Task Join_InvalidBusinessInputHasOriginalErrorAndNoPartialInsert(string? json, string code)
    {
        // 준비
        long owner = UserId();
        await using var host = await HostAsync();
        long room = await CreateAsync(host, owner);

        // 실행 / 검증
        await ErrorAsync(host, owner + 1, HttpMethod.Post, Path(room, "join"), code, json);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(1, await db.ChatRoomMembers.CountAsync(row => row.ChatRoomId == room));
    }

    private Task<ChatRuntimeTestHost> HostAsync(string? prefix = null)
    {
        return ChatRuntimeTestHost.StartAsync(fixture, prefix ?? Prefix(), configure: services =>
    {
        services.AddSingleton<IChatRoomAccountReader, SyntheticAccounts>();
        services.AddSingleton<IChatMembershipDirectory, SyntheticDirectory>();
    });
    }

    private static long UserId()
    {
        return Random.Shared.NextInt64(80000000, 90000000);
    }

    private static string Prefix()
    {
        return "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID") + ":open-membership:" + Guid.NewGuid().ToString("N");
    }

    private static string Path(long room, string action)
    {
        return $"/api/v1/chat/open-rooms/{room}/{action}";
    }

    private static async Task<long> CreateAsync(ChatRuntimeTestHost host, long owner, int capacity = 10)
    {
        var response = await SuccessAsync(host, owner, HttpMethod.Post, "/api/v1/chat/open-rooms",
            $"{{\"name\":\"합성 OPEN\",\"description\":\"합성 설명\",\"visibility\":\"PUBLIC\",\"maxMemberCount\":{capacity},\"ownerProfile\":{{\"nickname\":\"소유자\"}}}}", HttpStatusCode.Created);
        return response.GetProperty("id").GetInt64();
    }

    private static HttpRequestMessage Request(ChatRuntimeTestHost host, long user, HttpMethod method, string path, string? json)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.CreateToken(user));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static async Task<JsonElement> SuccessAsync(ChatRuntimeTestHost host, long user, HttpMethod method, string path,
        string? json = null, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var request = Request(host, user, method, path, json);
        using var response = await host.Client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"Expected {status}, received {response.StatusCode}: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("body").Clone();
    }

    private static async Task ErrorAsync(ChatRuntimeTestHost host, long user, HttpMethod method, string path, string code, string? json = null)
    {
        using var request = Request(host, user, method, path, json);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("body").GetProperty("errorCode").GetString());
    }

    private sealed class SyntheticAccounts : IChatRoomAccountReader
    {
        public Task EnsureUserExistsAsync(long? userId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<string> GetAuditIdentityAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult($"USER:{userId}");
        }

        public Task<ChatDirectPartner> GetDirectPartnerAsync(long userId, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException();
        }
    }

    private sealed class SyntheticDirectory : IChatMembershipDirectory
    {
        public Task<ChatMembershipUser?> FindByIdAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult<ChatMembershipUser?>(new(userId, $"synthetic-{userId}@example.invalid", "synthetic", $"synthetic-{userId}", "합성", null));
        }

        public Task<ChatMembershipUser?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException();
        }

        public Task<bool> AreFriendsAsync(long userId, long targetUserId, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException();
        }

        public Task<bool> IsBlockedBetweenAsync(long userId, long targetUserId, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException();
        }
    }
}
