using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.Rooms;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class OpenModerationRuntimeTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task Owner_assigns_and_revokes_ADMIN_with_actual_role_event_and_activity_notification()
    {
        // 준비: 운영 DI/실제 HTTP·EF·Redis·STOMP를 사용하며 공통 계정 조회만 합성이다.
        long owner = UserId();
        await using var host = await HostAsync();
        var room = await CreateAsync(host, owner);
        long target = await JoinAsync(host, owner + 1, room, "target");
        using var socket = await host.ConnectAsync(owner + 1);
        await socket.SubscribeAsync("role", $"/topic/chat/rooms/{room}");

        // 실행
        var assigned = await SuccessAsync(host, owner, HttpMethod.Post, Path(room, $"admins/{target}"));
        var changed = await socket.ReceiveEventAsync("chat.member.role.updated");

        // 검증
        Assert.Equal("ADMIN", assigned.GetProperty("role").GetString());
        Assert.Equal(target, changed.GetProperty("targetOpenChatMemberId").GetInt64());
        Assert.Equal("ADMIN", changed.GetProperty("role").GetString());
        await using (var read = await fixture.Contexts.CreateDbContextAsync())
        {
            Assert.Equal("ADMIN", (await read.ChatRoomMembers.SingleAsync(member => member.Id == target)).Role);
            var notification = Assert.Single(await read.ChatNotifications.Where(row => row.ChatRoomId == room
                && row.RecipientUserId == owner + 1 && row.NotificationType == "OPEN_CHAT_ROLE_CHANGED").ToListAsync());
            Assert.Contains("ADMIN", notification.PayloadJson);
        }

        // 실행 / 검증: ADMIN 해제도 프로필은 유지하고 역할만 변경한다.
        var revoked = await SuccessAsync(host, owner, HttpMethod.Delete, Path(room, $"admins/{target}"));
        Assert.Equal("MEMBER", revoked.GetProperty("role").GetString());
        Assert.Equal(assigned.GetProperty("memberCode").GetString(), revoked.GetProperty("memberCode").GetString());
    }

    [Fact]
    public async Task Ban_commits_snapshot_cursor_reset_system_message_and_cross_instance_private_event()
    {
        // 준비
        long owner = UserId();
        string prefix = Prefix();
        await using var first = await HostAsync(prefix);
        long room = await CreateAsync(first, owner);
        long target = await JoinAsync(first, owner + 1, room, "snapshot nickname");
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            await context.ChatRoomMembers.Where(member => member.Id == target).ExecuteUpdateAsync(setters => setters
                .SetProperty(member => member.LastReadMessageId, 99L).SetProperty(member => member.LastReadAt, ChatMySqlFixture.Epoch));
        }
        await using var second = await HostAsync(prefix);
        using var targetSocket = await second.ConnectAsync(owner + 1);
        await targetSocket.SubscribeAsync("private-ban", $"/user/queue/chat/open-rooms/{room}");
        using var ownerSocket = await second.ConnectAsync(owner);
        await ownerSocket.SubscribeAsync("room-ban", $"/topic/chat/rooms/{room}");

        // 실행: source transaction commit 뒤 Redis가 다른 app의 대상 큐와 방 구독으로 fan-out한다.
        var result = await BanAsync(first, owner, room, target);
        var targetEvent = await targetSocket.ReceiveEventAsync("chat.member.banned");
        var roomEvent = await ownerSocket.ReceiveEventAsync("chat.member.banned");

        // 검증
        long banId = result.GetProperty("banId").GetInt64();
        Assert.Equal(target, targetEvent.GetProperty("targetOpenChatMemberId").GetInt64());
        Assert.Equal(target, roomEvent.GetProperty("targetOpenChatMemberId").GetInt64());
        Assert.False(targetEvent.TryGetProperty("userId", out _));
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        var member = await read.ChatRoomMembers.SingleAsync(member => member.Id == target);
        Assert.False(member.Active);
        Assert.Equal("MEMBER", member.Role);
        Assert.Null(member.LastReadMessageId);
        Assert.Null(member.LastReadAt);
        Assert.NotNull(member.LeftAt);
        var ban = await read.OpenChatBans.SingleAsync(ban => ban.Id == banId);
        Assert.Equal("snapshot nickname", ban.NicknameSnapshot);
        Assert.Equal("OWNER", ban.BannedByRole);
        Assert.Equal("MEMBER", ban.TargetRoleSnapshot);
        Assert.Equal("reason", ban.Reason);
        Assert.Single(await read.ChatMessages.Where(message => message.ChatRoomId == room && message.MessageType == "SYSTEM"
            && message.Content.Contains("강제 퇴장")).ToListAsync());
        var notification = Assert.Single(await read.ChatNotifications.Where(row => row.ChatRoomId == room
            && row.RecipientUserId == owner + 1 && row.NotificationType == "OPEN_CHAT_KICKED").ToListAsync());
        Assert.Equal("open-ban:" + banId, notification.SourceEventKey);

        // 차단 이후 프로필/멤버 접근도 DB에서 실제 거절된다.
        using var denied = await SendAsync(first, owner + 1, HttpMethod.Get, Path(room, "members"));
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal("OPEN_CHAT_BANNED", (await BodyAsync(denied)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Admin_cannot_release_OWNER_ban_but_can_release_ADMIN_MEMBER_ban_without_rejoining_target()
    {
        // 준비
        long owner = UserId();
        await using var host = await HostAsync();
        long room = await CreateAsync(host, owner);
        long admin = await JoinAsync(host, owner + 1, room, "admin");
        long firstTarget = await JoinAsync(host, owner + 2, room, "first");
        long secondTarget = await JoinAsync(host, owner + 3, room, "second");
        await SuccessAsync(host, owner, HttpMethod.Post, Path(room, $"admins/{admin}"));
        var ownerBan = await BanAsync(host, owner, room, firstTarget);
        var adminBan = await BanAsync(host, owner + 1, room, secondTarget);

        // 실행 / 검증: 현재 actor가 ADMIN인 경우 ban 시점의 actor/target 역할을 사용한다.
        var list = await SuccessAsync(host, owner + 1, HttpMethod.Get, Path(room, "bans?size=1"));
        Assert.True(Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("releasable").GetBoolean());
        long cursor = list.GetProperty("nextCursorId").GetInt64();
        var older = await SuccessAsync(host, owner + 1, HttpMethod.Get, Path(room, $"bans?size=1&cursor={cursor}"));
        Assert.False(Assert.Single(older.GetProperty("items").EnumerateArray()).GetProperty("releasable").GetBoolean());
        await ErrorAsync(host, owner + 1, HttpMethod.Patch, Path(room, $"bans/{ownerBan.GetProperty("banId").GetInt64()}/release"), "OPEN_CHAT_BAN_RELEASE_FORBIDDEN");
        var released = await SuccessAsync(host, owner + 1, HttpMethod.Patch, Path(room, $"bans/{adminBan.GetProperty("banId").GetInt64()}/release"));
        Assert.False(released.GetProperty("active").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, released.GetProperty("releasedAt").ValueKind);
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        Assert.False((await read.ChatRoomMembers.SingleAsync(member => member.Id == secondTarget)).Active);
    }

    [Fact]
    public async Task Concurrent_bans_use_the_actual_room_lock_and_create_only_one_snapshot_and_system_message()
    {
        // 준비
        long owner = UserId();
        await using var host = await HostAsync();
        long room = await CreateAsync(host, owner);
        long target = await JoinAsync(host, owner + 1, room, "race target");
        string json = JsonSerializer.Serialize(new
        {
            targetOpenChatMemberId = target,
            reason = "reason"
        });

        // 실행: 별도 HTTP request scope/DbContext/connection이 같은 room lock에서 경합한다.
        var responses = await Task.WhenAll(
            SendAsync(host, owner, HttpMethod.Post, Path(room, "bans"), json),
            SendAsync(host, owner, HttpMethod.Post, Path(room, "bans"), json));

        // 검증
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.BadRequest);
        foreach (var response in responses)
        {
            response.Dispose();
        }

        await using var read = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(1, await read.OpenChatBans.CountAsync(ban => ban.ChatRoomId == room));
        Assert.Equal(1, await read.ChatMessages.CountAsync(message => message.ChatRoomId == room && message.Content.Contains("강제 퇴장")));
        Assert.Equal(1, await read.ChatNotifications.CountAsync(notification => notification.ChatRoomId == room && notification.NotificationType == "OPEN_CHAT_KICKED"));
    }

    [Fact]
    public async Task Authority_matrix_and_raw_HTTP_validation_prevent_mutations()
    {
        // 준비
        long owner = UserId();
        await using var host = await HostAsync();
        long room = await CreateAsync(host, owner);
        long first = await JoinAsync(host, owner + 1, room, "first");
        long second = await JoinAsync(host, owner + 2, room, "second");
        await SuccessAsync(host, owner, HttpMethod.Post, Path(room, $"admins/{first}"));
        await SuccessAsync(host, owner, HttpMethod.Post, Path(room, $"admins/{second}"));

        // 실행 / 검증
        await ErrorAsync(host, owner + 1, HttpMethod.Post, Path(room, $"admins/{second}"), "OPEN_CHAT_OWNER_ONLY");
        await ErrorAsync(host, owner + 1, HttpMethod.Post, Path(room, "bans"), "OPEN_CHAT_BAN_ROLE_FORBIDDEN",
            JsonSerializer.Serialize(new
            {
                targetOpenChatMemberId = second,
                reason = "reason"
            }));
        await ErrorAsync(host, owner + 1, HttpMethod.Post, Path(room, "bans"), "OPEN_CHAT_BAN_SELF_NOT_ALLOWED",
            JsonSerializer.Serialize(new
            {
                targetOpenChatMemberId = first,
                reason = "reason"
            }));
        using var invalid = await SendAsync(host, owner, HttpMethod.Post, Path(room, "bans"), "{\"targetOpenChatMemberId\":null,\"reason\":\"reason\"}");
        Assert.Equal(HttpStatusCode.InternalServerError, invalid.StatusCode);
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        Assert.Empty(await read.OpenChatBans.Where(ban => ban.ChatRoomId == room).ToListAsync());
    }

    [Fact]
    public async Task Ban_list_keeps_target_snapshot_current_actor_profile_and_CLOSED_query_access()
    {
        // 준비: 대상 snapshot과 actor 현재 프로필을 서로 다른 값으로 만든다.
        long owner = UserId();
        await using var host = await HostAsync();
        long room = await CreateAsync(host, owner);
        long target = await JoinAsync(host, owner + 1, room, "literal%_target");
        var result = await BanAsync(host, owner, room, target);
        long banId = result.GetProperty("banId").GetInt64();
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            await context.OpenChatMemberProfiles.Where(profile => profile.ChatRoomMemberId == target)
                .ExecuteUpdateAsync(setters => setters.SetProperty(profile => profile.Nickname, "changed after ban"));
        }
        await SuccessAsync(host, owner, HttpMethod.Patch, Path(room, "me/profile"), "{\"nickname\":\"owner now\"}");

        // 실행: 검색은 wildcard 패턴이 아니라 literal 부분 문자열이며 대상의 과거 이름을 사용한다.
        var list = await SuccessAsync(host, owner, HttpMethod.Get, Path(room, "bans?keyword=literal%25_"));
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());

        // 검증
        Assert.Equal("literal%_target", item.GetProperty("nickname").GetString());
        Assert.Equal("owner now", item.GetProperty("bannedBy").GetProperty("nickname").GetString());
        Assert.Equal("OWNER", item.GetProperty("bannedBy").GetProperty("role").GetString());
        Assert.False(item.TryGetProperty("targetUserId", out _));
        Assert.Equal(JsonValueKind.Null, list.GetProperty("nextCursorId").ValueKind);
        await ErrorAsync(host, owner + 1, HttpMethod.Get, Path(room, "bans?size=0"), "OPEN_CHAT_BANNED");

        // 실행 / 검증: 방 종료 후 운영 목록은 읽을 수 있지만 release/역할 변경은 차단한다.
        await SuccessAsync(host, owner, HttpMethod.Post, Path(room, "close"));
        Assert.Single((await SuccessAsync(host, owner, HttpMethod.Get, Path(room, "bans"))).GetProperty("items").EnumerateArray());
        await ErrorAsync(host, owner, HttpMethod.Patch, Path(room, $"bans/{banId}/release"), "OPEN_CHAT_ROOM_CLOSED");
    }

    [Fact]
    public async Task Missing_account_directory_rolls_back_ban_before_business_rows_are_changed()
    {
        // 준비
        long owner = UserId();
        await using var configured = await HostAsync();
        long room = await CreateAsync(configured, owner);
        long target = await JoinAsync(configured, owner + 1, room, "target");
        await using var missing = await HostAsync(directoryConfigured: false);

        // 실행
        using var response = await SendAsync(missing, owner, HttpMethod.Post, Path(room, "bans"),
            JsonSerializer.Serialize(new
            {
                targetOpenChatMemberId = target,
                reason = "reason"
            }));

        // 검증
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        Assert.True((await read.ChatRoomMembers.SingleAsync(member => member.Id == target)).Active);
        Assert.Empty(await read.OpenChatBans.Where(ban => ban.ChatRoomId == room).ToListAsync());
    }

    private Task<ChatRuntimeTestHost> HostAsync(string? prefix = null, bool directoryConfigured = true)
    {
        return ChatRuntimeTestHost.StartAsync(fixture, prefix ?? Prefix(), configure: services =>
        {
            services.AddSingleton<IChatRoomAccountReader, Accounts>();
            if (directoryConfigured)
            {
                services.AddSingleton<IChatMembershipDirectory, Directory>();
            }
        });
    }

    private static long UserId()
    {
        return Random.Shared.NextInt64(91000000, 98000000);
    }

    private static string Prefix()
    {
        return "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID") + ":moderation:" + Guid.NewGuid().ToString("N");
    }

    private static string Path(long room, string suffix)
    {
        return $"/api/v1/chat/open-rooms/{room}/{suffix}";
    }

    private static async Task<long> CreateAsync(ChatRuntimeTestHost host, long user)
    {
        return (await SuccessAsync(host, user, HttpMethod.Post, "/api/v1/chat/open-rooms",
                "{\"name\":\"synthetic moderation\",\"description\":\"description\",\"visibility\":\"PUBLIC\",\"ownerProfile\":{\"nickname\":\"owner\"}}", HttpStatusCode.Created)).GetProperty("id").GetInt64();
    }

    private static async Task<long> JoinAsync(ChatRuntimeTestHost host, long user, long room, string nickname)
    {
        return (await SuccessAsync(host, user, HttpMethod.Post, Path(room, "join"), JsonSerializer.Serialize(new
        {
            profile = new
            {
                nickname
            }
        })))
                .GetProperty("myOpenProfile").GetProperty("openChatMemberId").GetInt64();
    }

    private static Task<JsonElement> BanAsync(ChatRuntimeTestHost host, long actor, long room, long target)
    {
        return SuccessAsync(host, actor, HttpMethod.Post, Path(room, "bans"), JsonSerializer.Serialize(new
        {
            targetOpenChatMemberId = target,
            reason = " reason "
        }));
    }

    private static async Task<JsonElement> SuccessAsync(ChatRuntimeTestHost host, long user, HttpMethod method, string path,
        string? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await SendAsync(host, user, method, path, body);
        Assert.Equal(expected, response.StatusCode);
        return await BodyAsync(response);
    }
    private static async Task ErrorAsync(ChatRuntimeTestHost host, long user, HttpMethod method, string path, string code, string? body = null)
    {
        using var response = await SendAsync(host, user, method, path, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, (await BodyAsync(response)).GetProperty("errorCode").GetString());
    }
    private static async Task<HttpResponseMessage> SendAsync(ChatRuntimeTestHost host, long user, HttpMethod method, string path, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.CreateToken(user));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return await host.Client.SendAsync(request);
    }
    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("body").Clone();
    }
    private sealed class Accounts : IChatRoomAccountReader
    {
        public Task EnsureUserExistsAsync(long? id, CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public Task<string> GetAuditIdentityAsync(long id, CancellationToken token)
        {
            return Task.FromResult("USER:" + id);
        }

        public Task<ChatDirectPartner> GetDirectPartnerAsync(long id, CancellationToken token)
        {
            throw new InvalidOperationException();
        }
    }
    private sealed class Directory : IChatMembershipDirectory
    {
        public Task<ChatMembershipUser?> FindByIdAsync(long id, CancellationToken token)
        {
            return Task.FromResult<ChatMembershipUser?>(
            new(id, $"synthetic-{id}@example.invalid", "synthetic", "synthetic-" + id, "synthetic", null));
        }

        public Task<ChatMembershipUser?> FindByPublicIdAsync(string id, CancellationToken token)
        {
            throw new InvalidOperationException();
        }

        public Task<bool> AreFriendsAsync(long first, long second, CancellationToken token)
        {
            throw new InvalidOperationException();
        }

        public Task<bool> IsBlockedBetweenAsync(long first, long second, CancellationToken token)
        {
            throw new InvalidOperationException();
        }
    }
}
