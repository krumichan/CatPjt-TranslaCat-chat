using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TranslaCat.Chat.Api.Authentication;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class ChatAiManagementRuntimeTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task CrudAndSafeProfile_UseRealHttpDatabaseAndCrossAppMembershipEvent()
    {
        // 준비
        var room = await ManagedRoomAsync();
        string prefix = Prefix();
        await using var writer = await HostAsync(prefix);
        await using var receiver = await HostAsync(prefix);
        using var socket = await receiver.ConnectAsync(room.UserId);
        await socket.SubscribeAsync("members", $"/topic/chat/rooms/{room.RoomId}");

        // 실행
        var created = await SendAsync(writer, room.UserId, HttpMethod.Post, Members(room.RoomId), Profile);
        var member = created.GetProperty("body");
        long id = member.GetProperty("aiMemberId").GetInt64();
        var changed = await socket.ReceiveEventAsync("chat.members.changed");
        var safe = await SendAsync(writer, room.UserId, HttpMethod.Get, Members(room.RoomId) + $"/{id}/profile");
        var updated = await SendAsync(writer, room.UserId, HttpMethod.Patch, Members(room.RoomId) + $"/{id}", Profile.Replace("합성 AI", "변경 AI"));
        var deleted = await SendAsync(writer, room.UserId, HttpMethod.Delete, Members(room.RoomId) + $"/{id}");

        // 검증 — persona는 관리 응답에만 있고 공개 프로필에는 없다. 삭제는 soft-delete이며 데이터는 남는다.
        Assert.Equal(201, created.GetProperty("resultCode").GetInt32());
        Assert.Equal(room.RoomId, changed.GetProperty("roomId").GetInt64());
        Assert.Equal("en", member.GetProperty("originalLanguageCode").GetString());
        Assert.Equal(JsonValueKind.Null, member.GetProperty("profileImageUrl").ValueKind);
        Assert.False(safe.GetProperty("body").TryGetProperty("personaPrompt", out _));
        Assert.False(safe.GetProperty("body").TryGetProperty("aiAgentId", out _));
        Assert.Equal("변경 AI", updated.GetProperty("body").GetProperty("nickname").GetString());
        Assert.False(deleted.GetProperty("body").GetProperty("active").GetBoolean());
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        var stored = await db.ChatRoomAiMembers.SingleAsync(row => row.Id == id);
        Assert.NotNull(stored.DeletedAt);
        Assert.NotNull(stored.LeftAt);
        Assert.False((await db.ChatAiAgents.SingleAsync(row => row.Id == stored.AiAgentId)).Active);
        Assert.True(await db.ChatRoomAiSettings.AnyAsync(row => row.ChatRoomId == room.RoomId));
    }

    [Fact]
    public async Task MemberCanReadSettingsAndSafeProfileButCannotManagePersona()
    {
        // 준비
        var room = await ManagedRoomAsync();
        long user = room.UserId + 1;
        await AddHumanAsync(room.RoomId, user);
        await using var host = await HostAsync();
        var created = await SendAsync(host, room.UserId, HttpMethod.Post, Members(room.RoomId), Profile);
        long id = created.GetProperty("body").GetProperty("aiMemberId").GetInt64();

        // 실행 / 검증 — GROUP 일반 회원과 외부 사용자의 접근을 각각 구분한다.
        await SendAsync(host, user, HttpMethod.Get, Settings(room.RoomId));
        await SendAsync(host, user, HttpMethod.Get, Members(room.RoomId) + $"/{id}/profile");
        await SendAsync(host, user, HttpMethod.Get, Members(room.RoomId) + $"/{id}", expected: HttpStatusCode.BadRequest, code: "CHAT_AI_ROOM_MANAGEMENT_ACCESS_DENIED");
        await SendAsync(host, user, HttpMethod.Post, Members(room.RoomId), Profile, HttpStatusCode.BadRequest, "CHAT_AI_ROOM_MANAGEMENT_ACCESS_DENIED");
        await SendAsync(host, user + 1, HttpMethod.Get, Members(room.RoomId) + $"/{id}/profile", expected: HttpStatusCode.BadRequest, code: "CHAT_AI_ROOM_MEMBER_ACCESS_DENIED");
    }

    [Fact]
    public async Task RoomSettings_PatchMergesNullableValuesAndEmitsOnlyDisclosureChange()
    {
        // 준비
        var room = await ManagedRoomAsync();
        await using var host = await HostAsync();
        using var socket = await host.ConnectAsync(room.UserId);
        await socket.SubscribeAsync("members", $"/topic/chat/rooms/{room.RoomId}");

        // 실행
        var defaults = (await SendAsync(host, room.UserId, HttpMethod.Get, Settings(room.RoomId))).GetProperty("body");
        var changed = (await SendAsync(host, room.UserId, HttpMethod.Patch, Settings(room.RoomId),
            "{\"disclosureType\":1,\"mentionPermission\":\"OWNER_ADMIN_ONLY\",\"conversationEnabled\":\"false\",\"revivalEnabled\":null}")).GetProperty("body");
        var eventBody = await socket.ReceiveEventAsync("chat.members.changed");

        // 검증
        Assert.Equal("PUBLIC", defaults.GetProperty("disclosureType").GetString());
        Assert.True(defaults.GetProperty("revivalEnabled").GetBoolean());
        Assert.Equal("PRIVATE", changed.GetProperty("disclosureType").GetString());
        Assert.False(changed.GetProperty("conversationEnabled").GetBoolean());
        Assert.True(changed.GetProperty("revivalEnabled").GetBoolean());
        Assert.Equal(room.RoomId, eventBody.GetProperty("roomId").GetInt64());
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal("PRIVATE", (await db.ChatRoomAiSettings.SingleAsync(row => row.ChatRoomId == room.RoomId)).DisclosureType);
    }

    [Fact]
    public async Task SystemSettings_RequireVerifiedAdminAndMergeBeforeValidation()
    {
        // 준비 — 관리자 role은 합성 identity resolver에서만 지정하며 토큰의 임의 claim을 신뢰하지 않는다.
        long admin = Random.Shared.NextInt64(91000000, 92000000);
        await using var host = await HostAsync(admin: admin);
        const string path = "/api/v1/admin/chat/ai-settings";
        await SendAsync(host, admin + 1, HttpMethod.Get, path, expected: HttpStatusCode.Forbidden);
        var before = (await SendAsync(host, admin, HttpMethod.Get, path)).GetProperty("body");
        int oldCooldown = before.GetProperty("conversationCooldownSeconds").GetInt32();
        try
        {
            // 실행 / 검증 — 잘못된 merged 지연은 전체 patch를 rollback한다.
            await SendAsync(host, admin, HttpMethod.Patch, path, "{\"responseDelayMinMillis\":10001,\"conversationCooldownSeconds\":999}",
                HttpStatusCode.BadRequest, "CHAT_AI_SETTING_INVALID");
            var afterFailure = (await SendAsync(host, admin, HttpMethod.Get, path)).GetProperty("body");
            Assert.Equal(oldCooldown, afterFailure.GetProperty("conversationCooldownSeconds").GetInt32());
            var after = (await SendAsync(host, admin, HttpMethod.Patch, path, $"{{\"conversationCooldownSeconds\":\"{oldCooldown + 1}\"}}")).GetProperty("body");
            Assert.Equal(oldCooldown + 1, after.GetProperty("conversationCooldownSeconds").GetInt32());
            Assert.Equal("10:00:00", after.GetProperty("revivalAllowedStartTime").GetString());
        }
        finally
        {
            // 합성 catalog의 공통 DEFAULT 행을 원상 값으로 돌려 다른 AI 시험의 설정을 바꾸지 않는다.
            await SendAsync(host, admin, HttpMethod.Patch, path, $"{{\"conversationCooldownSeconds\":{oldCooldown}}}");
        }
    }

    [Fact]
    public async Task ConcurrentCreation_LocksRoomAndPreservesMaximum()
    {
        // 준비 — 원본 기본 max=2 중 하나를 채워 두고 별도 DB connection으로 두 요청의 대기를 재현한다.
        var room = await ManagedRoomAsync();
        await using var host = await HostAsync();
        var settings = (await SendAsync(host, room.UserId, HttpMethod.Get, Settings(room.RoomId))).GetProperty("body");
        int maximum = settings.GetProperty("maxAiMembersPerRoom").GetInt32();
        for (int index = 0; index < maximum - 1; index++)
        {
            await SendAsync(host, room.UserId, HttpMethod.Post, Members(room.RoomId), Profile);
        }

        await using var blocker = await fixture.Contexts.CreateDbContextAsync();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.ChatRooms.FromSqlInterpolated($"SELECT * FROM chat_room WHERE id = {room.RoomId} FOR UPDATE").ToListAsync();

        // 실행
        using var firstRequest = Request(host, room.UserId, HttpMethod.Post, Members(room.RoomId), Profile);
        using var secondRequest = Request(host, room.UserId, HttpMethod.Post, Members(room.RoomId), Profile);
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
        var results = await Task.WhenAll(first, second);

        // 검증
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal(maximum, await observer.ChatRoomAiMembers.CountAsync(row => row.ChatRoomId == room.RoomId && row.Active));
        foreach (var response in results)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task MissingStorageAndClosedOpenRoom_FailWithoutMutation()
    {
        // 준비
        var room = await ManagedRoomAsync("OPEN");
        await using var host = await HostAsync();
        var created = await SendAsync(host, room.UserId, HttpMethod.Post, Members(room.RoomId), Profile);
        long id = created.GetProperty("body").GetProperty("aiMemberId").GetInt64();
        long agentId = created.GetProperty("body").GetProperty("aiAgentId").GetInt64();
        await using (var db = await fixture.Contexts.CreateDbContextAsync())
        {
            await db.ChatAiAgents.Where(row => row.Id == agentId).ExecuteUpdateAsync(update => update.SetProperty(row => row.ProfileImageObjectKey, "synthetic-image"));
        }

        // 실행 / 검증 — 응답 URL 준비 실패는 같은 transaction의 profile 변경을 rollback한다.
        await SendAsync(host, room.UserId, HttpMethod.Patch, Members(room.RoomId) + $"/{id}", Profile.Replace("합성 AI", "롤백 AI"), HttpStatusCode.ServiceUnavailable);
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal("합성 AI", (await read.ChatAiAgents.SingleAsync(row => row.Id == agentId)).Nickname);
        await read.OpenChatRooms.Where(row => row.ChatRoomId == room.RoomId).ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, "CLOSED"));
        await SendAsync(host, room.UserId, HttpMethod.Delete, Members(room.RoomId) + $"/{id}", expected: HttpStatusCode.BadRequest, code: "OPEN_CHAT_ROOM_CLOSED");
        Assert.True((await read.ChatRoomAiMembers.SingleAsync(row => row.Id == id)).Active);

        // 종료방의 과거 공개 프로필 조회는 보존한다. 이미지 adapter 필요 조건만 제거해 확인한다.
        await read.ChatAiAgents.Where(row => row.Id == agentId).ExecuteUpdateAsync(update => update.SetProperty(row => row.ProfileImageObjectKey, (string?)null));
        await SendAsync(host, room.UserId, HttpMethod.Get, Members(room.RoomId) + $"/{id}/profile");
    }

    [Theory]
    [InlineData("{\"nickname\":\"\"}")]
    [InlineData("{\"nickname\":{},\"originalLanguageCode\":\"en\",\"personaPrompt\":\"합성\"}")]
    public async Task InvalidProfileBinding_PreservesOriginal500AndDoesNotWrite(string json)
    {
        // 준비
        var room = await ManagedRoomAsync();
        await using var host = await HostAsync();

        // 실행 / 검증
        await SendAsync(host, room.UserId, HttpMethod.Post, Members(room.RoomId), json, HttpStatusCode.InternalServerError);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.False(await db.ChatRoomAiMembers.AnyAsync(row => row.ChatRoomId == room.RoomId));
    }

    private async Task<SeededReadRoom> ManagedRoomAsync(string type = "GROUP")
    {
        var room = await fixture.SeedReadRoomAsync(type, Random.Shared.NextInt64(93000000, 94000000));
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        await db.ChatRoomMembers.Where(row => row.Id == room.MemberId).ExecuteUpdateAsync(update => update.SetProperty(row => row.Role, "OWNER"));
        if (type == "OPEN")
        {
            db.OpenChatRooms.Add(new OpenChatRoomEntity
            {
                ChatRoomId = room.RoomId,
                Visibility = "PUBLIC",
                Status = "ACTIVE",
                MaxMemberCount = 50,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            await db.SaveChangesAsync();
        }
        return room;
    }

    private async Task AddHumanAsync(long roomId, long userId)
    {
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        db.ChatRoomMembers.Add(new ChatRoomMemberEntity
        {
            ChatRoomId = roomId,
            UserId = userId,
            Role = "MEMBER",
            JoinedAt = ChatMySqlFixture.Epoch,
            CreatedAt = ChatMySqlFixture.Epoch,
            UpdatedAt = ChatMySqlFixture.Epoch
        });
        await db.SaveChangesAsync();
    }

    private Task<ChatRuntimeTestHost> HostAsync(string? prefix = null, long? admin = null)
    {
        return ChatRuntimeTestHost.StartAsync(fixture, prefix ?? Prefix(), configure: services =>
        {
            services.AddSingleton<IChatRoomAccountReader, SyntheticAccounts>();
            if (admin is not null)
            {
                services.AddSingleton<IChatIdentityResolver>(new SyntheticAdminResolver(admin.Value));
            }
        });
    }

    private const string Profile = "{\"nickname\":\" 합성 AI \",\"bio\":null,\"originalLanguageCode\":\" EN \",\"personaPrompt\":\"합성 fixture persona\"}";
    private static string Prefix()
    {
        return "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID") + ":ai-management:" + Guid.NewGuid().ToString("N");
    }

    private static string Members(long roomId)
    {
        return $"/api/v1/chat/rooms/{roomId}/ai-members";
    }

    private static string Settings(long roomId)
    {
        return $"/api/v1/chat/rooms/{roomId}/ai-settings";
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

    private static async Task<JsonElement> SendAsync(ChatRuntimeTestHost host, long user, HttpMethod method, string path, string? json = null,
        HttpStatusCode expected = HttpStatusCode.OK, string? code = null)
    {
        using var request = Request(host, user, method, path, json);
        using var response = await host.Client.SendAsync(request);
        string content = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {content}");
        if (string.IsNullOrEmpty(content))
        {
            return default;
        }

        using var body = JsonDocument.Parse(content);
        if (code is not null)
        {
            Assert.Equal(code, body.RootElement.GetProperty("body").GetProperty("errorCode").GetString());
        }

        return body.RootElement.Clone();
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

    private sealed class SyntheticAdminResolver(long adminId) : IChatIdentityResolver
    {
        public Task<ChatResolvedIdentity?> ResolveAsync(ChatIdentityLookup lookup, CancellationToken cancellationToken)
        {
            return Task.FromResult<ChatResolvedIdentity?>(new(lookup.TokenUserId, lookup.Subject, lookup.TokenUserId == adminId ? "ROLE_ADMIN" : "ROLE_USER", true));
        }
    }
}
