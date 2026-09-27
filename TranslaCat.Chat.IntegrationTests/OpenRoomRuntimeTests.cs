using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Infrastructure.Persistence.OpenRooms;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class OpenRoomRuntimeTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task Creation_preserves_in_memory_time_before_later_DATABASE_precision_roundtrip()
    {
        // 준비: 이 한 사례는 HTTP가 아니라 실제 EF 생성 transaction의 시각 경계를 검증한다.
        var store = new EfOpenRoomStore(fixture.Contexts, NullLogger<EfOpenRoomStore>.Instance, new SyntheticAccounts());
        var now = ChatMySqlFixture.Epoch.AddTicks(1234567);

        // 실행
        var result = await store.CreateAsync(UserId(), OpenRoomPolicy.ValidateCreate(
            new("precision", "synthetic", "PUBLIC", null, new("nickname", null))), now, CancellationToken.None);

        // 검증
        Assert.Equal(now, result.CreatedAt);
        Assert.Equal(now, result.MyOpenProfile!.JoinedAt);
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        var stored = await context.ChatRooms.SingleAsync(room => room.Id == result.Id);
        Assert.Equal(0, stored.CreatedAt.Ticks % 10);
        Assert.InRange(Math.Abs(stored.CreatedAt.Ticks - now.Ticks), 0, 10);
    }

    [Fact]
    public async Task Create_Uses_atomic_CHAT_rows_anonymous_profile_and_language_snapshot()
    {
        // 준비
        long user = UserId();
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            context.UserChatLanguageSettings.Add(new UserChatLanguageSettingEntity
            {
                UserId = user,
                OriginalLanguageCode = "en",
                TranslationLanguageCode = "ko",
                ShowOriginal = false,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            await context.SaveChangesAsync();
        }
        await using var host = await HostAsync();

        // 실행
        var created = await CreateAsync(host, user, "  합성 OPEN 😺  ");

        // 검증: 일반 사용자 id/email을 공개 OPEN profile에 섞지 않는다.
        long roomId = created.GetProperty("id").GetInt64();
        var profile = created.GetProperty("myOpenProfile");
        Assert.Equal("합성 OPEN 😺", created.GetProperty("name").GetString());
        Assert.Equal(50, created.GetProperty("maxMemberCount").GetInt32());
        Assert.Equal("ALREADY_JOINED", created.GetProperty("joinBlockedReason").GetString());
        Assert.False(created.GetProperty("joinable").GetBoolean());
        Assert.Equal("OWNER", created.GetProperty("myRole").GetString());
        Assert.False(profile.TryGetProperty("userId", out _));
        Assert.False(profile.TryGetProperty("email", out _));
        Assert.Null(profile.GetProperty("online").GetString());
        Assert.Matches("^OC-[ABCDEFGHJKLMNPQRSTUVWXYZ23456789]{5}$", profile.GetProperty("memberCode").GetString()!);
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        var membership = await read.ChatRoomMembers.SingleAsync(member => member.ChatRoomId == roomId);
        Assert.Equal("en", membership.OriginalLanguageCode);
        Assert.Equal("ko", membership.TranslationLanguageCode);
        Assert.False(membership.ShowOriginal);
        Assert.Null(membership.LastReadAt);
        Assert.Null(membership.LastReadMessageId);
        Assert.Equal(profile.GetProperty("openChatMemberId").GetInt64(), membership.Id);
        Assert.Single(await read.OpenChatRooms.Where(room => room.ChatRoomId == roomId).ToListAsync());
    }

    [Theory]
    [InlineData("{\"name\":\"x\",\"description\":\"d\",\"visibility\":\"PUBLIC\",\"ownerProfile\":null}")]
    [InlineData("{\"name\":\"x\",\"description\":\"d\",\"visibility\":\"INVALID\",\"ownerProfile\":{\"nickname\":\"n\"}}")]
    [InlineData("{\"name\":\"x\",\"description\":\"d\",\"visibility\":\"PUBLIC\",\"maxMemberCount\":101,\"ownerProfile\":{\"nickname\":\"n\"}}")]
    public async Task Invalid_create_HTTP_contract_never_writes_a_room(string body)
    {
        // 준비
        await using var host = await HostAsync();
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        int before = await read.ChatRooms.CountAsync();

        // 실행
        using var response = await SendAsync(host, UserId(), HttpMethod.Post, "/api/v1/chat/open-rooms", body);

        // 검증
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(before, await read.ChatRooms.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_account_or_mismatched_owner_image_does_not_leave_partial_rows(bool wrongImage)
    {
        // 준비: 잘못된 image key는 ID를 받은 이후 거절되므로 outer rollback까지 확인한다.
        await using var host = await HostAsync(accountConfigured: wrongImage);
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        int before = await read.ChatRooms.CountAsync();
        int membersBefore = await read.ChatRoomMembers.CountAsync();
        string image = wrongImage ? ",\"profileImageObjectKey\":\"open-chat-profiles/9223372036854775807/image.png\"" : "";

        // 실행
        using var response = await SendAsync(host, UserId(), HttpMethod.Post, "/api/v1/chat/open-rooms",
            "{\"name\":\"x\",\"description\":\"d\",\"visibility\":\"PUBLIC\",\"ownerProfile\":{\"nickname\":\"n\"" + image + "}}");

        // 검증
        Assert.Equal(wrongImage ? HttpStatusCode.BadRequest : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(before, await read.ChatRooms.CountAsync());
        Assert.Equal(membersBefore, await read.ChatRoomMembers.CountAsync());
    }

    [Fact]
    public async Task Public_search_escapes_wildcards_and_preserves_id_pagination_and_UNLISTED_detail()
    {
        // 준비: 다른 실행 데이터와 격리되는 검색어다.
        long user = UserId();
        string keyword = Guid.NewGuid().ToString("N") + "%_";
        await using var host = await HostAsync();
        var first = await CreateAsync(host, user, keyword + " first");
        var second = await CreateAsync(host, user, keyword + " second");
        var hidden = await CreateAsync(host, user, keyword + " hidden", "UNLISTED");
        await CreateAsync(host, user, keyword.Replace("%_", "XY") + " no literal wildcard");

        // 실행
        var page = await GetAsync(host, user + 1, "/api/v1/chat/open-rooms?size=1&keyword=" + Uri.EscapeDataString(keyword));
        long cursor = page.GetProperty("nextCursorId").GetInt64();
        var next = await GetAsync(host, user + 1, "/api/v1/chat/open-rooms?size=1&cursorId=" + cursor + "&keyword=" + Uri.EscapeDataString(keyword));
        var detail = await GetAsync(host, user + 1, "/api/v1/chat/open-rooms/" + hidden.GetProperty("id").GetInt64());

        // 검증
        Assert.Equal(second.GetProperty("id").GetInt64(), Assert.Single(page.GetProperty("openChatRooms").EnumerateArray()).GetProperty("id").GetInt64());
        Assert.Equal(first.GetProperty("id").GetInt64(), Assert.Single(next.GetProperty("openChatRooms").EnumerateArray()).GetProperty("id").GetInt64());
        Assert.False(next.GetProperty("hasNext").GetBoolean());
        Assert.Equal("UNLISTED", detail.GetProperty("visibility").GetString());
        Assert.True(detail.GetProperty("joinable").GetBoolean());
    }

    [Fact]
    public async Task Banned_user_sees_public_owner_in_list_but_not_detail_or_member_profiles()
    {
        // 준비
        long owner = UserId();
        long banned = owner + 1;
        string keyword = Guid.NewGuid().ToString("N");
        await using var host = await HostAsync();
        var created = await CreateAsync(host, owner, keyword);
        long room = created.GetProperty("id").GetInt64();
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            var target = new ChatRoomMemberEntity
            {
                ChatRoomId = room,
                UserId = banned,
                Role = "MEMBER",
                Active = false,
                JoinedAt = ChatMySqlFixture.Epoch,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            };
            context.ChatRoomMembers.Add(target);
            await context.SaveChangesAsync();
            context.OpenChatBans.Add(new OpenChatBanEntity
            {
                ChatRoomId = room,
                TargetUserId = banned,
                TargetChatRoomMemberId = target.Id,
                TargetMemberCode = "OC-SYNTH",
                NicknameSnapshot = "synthetic",
                TargetRoleSnapshot = "MEMBER",
                LastJoinedAtSnapshot = ChatMySqlFixture.Epoch,
                BannedByMemberId = created.GetProperty("myOpenProfile").GetProperty("openChatMemberId").GetInt64(),
                BannedByRole = "OWNER",
                Reason = "synthetic",
                BannedAt = ChatMySqlFixture.Epoch,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            await context.SaveChangesAsync();
        }

        // 실행
        var detail = await GetAsync(host, banned, $"/api/v1/chat/open-rooms/{room}");
        var list = await GetAsync(host, banned, "/api/v1/chat/open-rooms?keyword=" + keyword);
        using var denied = await SendAsync(host, banned, HttpMethod.Get, $"/api/v1/chat/open-rooms/{room}/members");

        // 검증
        Assert.Equal("BANNED", detail.GetProperty("joinBlockedReason").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("ownerProfile").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("myOpenProfile").ValueKind);
        Assert.Equal(JsonValueKind.Object, Assert.Single(list.GetProperty("openChatRooms").EnumerateArray()).GetProperty("ownerProfile").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal("OPEN_CHAT_BANNED", (await BodyAsync(denied)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Nickname_update_commits_and_cross_instance_Redis_STOMP_event_keeps_member_identity()
    {
        // 준비: 계정 조회만 합성이고 HTTP/EF/Redis/두 app/native STOMP는 실제다.
        long owner = UserId();
        string prefix = Prefix();
        await using var first = await HostAsync(prefix);
        var created = await CreateAsync(first, owner, "wire test");
        long room = created.GetProperty("id").GetInt64();
        await using var second = await HostAsync(prefix);
        using var socket = await second.ConnectAsync(owner);
        await socket.SubscribeAsync("open-profile", $"/topic/chat/rooms/{room}");

        // 실행
        using var response = await SendAsync(first, owner, HttpMethod.Patch, $"/api/v1/chat/open-rooms/{room}/me/profile", "{\"nickname\":\"  changed  \"}");
        var changed = await socket.ReceiveEventAsync("chat.open-profile.updated");

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("changed", changed.GetProperty("nickname").GetString());
        Assert.Equal(created.GetProperty("myOpenProfile").GetProperty("openChatMemberId").GetInt64(), changed.GetProperty("openChatMemberId").GetInt64());
        Assert.False(changed.TryGetProperty("userId", out _));
        var profile = await GetAsync(second, owner, $"/api/v1/chat/open-rooms/{room}/me/profile");
        Assert.Equal("changed", profile.GetProperty("nickname").GetString());
        Assert.True(profile.GetProperty("online").GetBoolean());
        Assert.Equal(created.GetProperty("myOpenProfile").GetProperty("memberCode").GetString(), profile.GetProperty("memberCode").GetString());
    }

    [Fact]
    public async Task Profile_url_failure_rolls_back_nickname_and_image_delete_runs_after_commit()
    {
        // 준비
        long owner = UserId();
        var storage = new SyntheticStorage(fixture.Contexts);
        await using var host = await HostAsync(storage: storage);
        var created = await CreateAsync(host, owner, "storage rollback");
        long room = created.GetProperty("id").GetInt64();
        long member = created.GetProperty("myOpenProfile").GetProperty("openChatMemberId").GetInt64();
        string key = $"open-chat-profiles/{member}/synthetic.png";
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            await context.OpenChatMemberProfiles.Where(profile => profile.ChatRoomMemberId == member)
                .ExecuteUpdateAsync(setters => setters.SetProperty(profile => profile.ProfileImageObjectKey, key));
        }
        storage.FailResolve = true;

        // 실행 / 검증: 저장 후 projection 오류가 outer transaction을 rollback한다.
        using var failed = await SendAsync(host, owner, HttpMethod.Patch, $"/api/v1/chat/open-rooms/{room}/me/profile", "{\"nickname\":\"must rollback\"}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        await using (var read = await fixture.Contexts.CreateDbContextAsync())
        {
            Assert.Equal("nickname", (await read.OpenChatMemberProfiles.SingleAsync(profile => profile.ChatRoomMemberId == member)).Nickname);
        }

        // 실행 / 검증: object 삭제 port가 독립 context에서 commit된 null을 관측한다.
        storage.FailResolve = false;
        using var deleted = await SendAsync(host, owner, HttpMethod.Delete, $"/api/v1/chat/open-rooms/{room}/me/profile-image");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(key, Assert.Single(storage.Deleted));
        Assert.True(storage.ObservedCommittedNull);
        Assert.Equal(JsonValueKind.Null, (await BodyAsync(deleted)).GetProperty("profileImageUrl").ValueKind);
    }

    [Fact]
    public async Task Closed_room_keeps_detail_and_history_profile_but_rejects_profile_mutation()
    {
        // 준비
        long owner = UserId();
        await using var host = await HostAsync();
        var created = await CreateAsync(host, owner, "closed test", "UNLISTED");
        long room = created.GetProperty("id").GetInt64();
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            await context.OpenChatRooms.Where(open => open.ChatRoomId == room)
                .ExecuteUpdateAsync(setters => setters.SetProperty(open => open.Status, "CLOSED"));
        }

        // 실행
        var detail = await GetAsync(host, owner, $"/api/v1/chat/open-rooms/{room}");
        var profile = await GetAsync(host, owner, $"/api/v1/chat/open-rooms/{room}/me/profile");
        using var failed = await SendAsync(host, owner, HttpMethod.Patch, $"/api/v1/chat/open-rooms/{room}/me/profile", "{\"nickname\":null}");

        // 검증: CLOSED 검사가 nickname required보다 우선이다.
        Assert.Equal("ROOM_CLOSED", detail.GetProperty("joinBlockedReason").GetString());
        Assert.Equal("nickname", profile.GetProperty("nickname").GetString());
        Assert.Equal("OPEN_CHAT_ROOM_CLOSED", (await BodyAsync(failed)).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task Member_list_uses_real_Presence_and_PRIVATE_AI_hides_online_without_leaking_persona()
    {
        // 준비
        long owner = UserId();
        await using var host = await HostAsync();
        var created = await CreateAsync(host, owner, "member display");
        long room = created.GetProperty("id").GetInt64();
        using var socket = await host.ConnectAsync(owner);

        // 실행 / 검증: 실제 Redis 등록을 반영한 활성 사람 프로필만 반환한다.
        var publicMembers = await GetAsync(host, owner, $"/api/v1/chat/open-rooms/{room}/members");
        Assert.True(Assert.Single(publicMembers.GetProperty("members").EnumerateArray()).GetProperty("online").GetBoolean());

        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            var inactive = new ChatRoomMemberEntity
            {
                ChatRoomId = room,
                UserId = owner + 1,
                Role = "MEMBER",
                Active = false,
                JoinedAt = ChatMySqlFixture.Epoch,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            };
            var agent = new ChatAiAgentEntity
            {
                Nickname = "synthetic AI",
                OriginalLanguageCode = "ko",
                PersonaPrompt = "synthetic private persona",
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            };
            context.ChatRoomMembers.Add(inactive);
            context.ChatAiAgents.Add(agent);
            await context.SaveChangesAsync();
            context.OpenChatMemberProfiles.Add(new OpenChatMemberProfileEntity
            {
                ChatRoomMemberId = inactive.Id,
                MemberCode = "OC-" + Guid.NewGuid().ToString("N")[..12],
                Nickname = "past member",
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            context.ChatRoomAiMembers.Add(new ChatRoomAiMemberEntity
            {
                ChatRoomId = room,
                AiAgentId = agent.Id,
                JoinedAt = ChatMySqlFixture.Epoch,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            context.ChatRoomAiSettings.Add(new ChatRoomAiSettingEntity
            {
                ChatRoomId = room,
                DisclosureType = "PRIVATE",
                MentionPermission = "ALL_MEMBERS",
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            await context.SaveChangesAsync();
        }

        // 실행 / 검증: PRIVATE는 온라인만 숨기며 원본 안전한 AI 표시 DTO는 유지한다.
        var privateMembers = await GetAsync(host, owner, $"/api/v1/chat/open-rooms/{room}/members");
        Assert.Equal(JsonValueKind.Null, Assert.Single(privateMembers.GetProperty("members").EnumerateArray()).GetProperty("online").ValueKind);
        var ai = Assert.Single(privateMembers.GetProperty("aiMembers").EnumerateArray());
        Assert.Equal("synthetic AI", ai.GetProperty("nickname").GetString());
        Assert.False(ai.TryGetProperty("personaPrompt", out _));
        Assert.Equal("PRIVATE", privateMembers.GetProperty("aiDisclosureType").GetString());
        var detail = await GetAsync(host, owner, $"/api/v1/chat/open-rooms/{room}");
        Assert.Equal(1, detail.GetProperty("ai").GetProperty("aiMemberCount").GetInt32());
        Assert.Equal("PRIVATE", detail.GetProperty("ai").GetProperty("disclosureType").GetString());

        // 과거 본인 프로필은 상세에 보존되지만 현재 멤버 조회 권한은 주지 않는다.
        var past = await GetAsync(host, owner + 1, $"/api/v1/chat/open-rooms/{room}");
        Assert.False(past.GetProperty("myOpenProfile").GetProperty("active").GetBoolean());
        Assert.Equal(JsonValueKind.Null, past.GetProperty("myRole").ValueKind);
        using var denied = await SendAsync(host, owner + 1, HttpMethod.Get, $"/api/v1/chat/open-rooms/{room}/me/profile");
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
    }

    [Fact]
    public async Task Profile_update_waits_for_other_connection_room_lock_and_observes_committed_close()
    {
        // 준비: 별도 실제 MySQL transaction이 같은 OPEN 방 lock을 소유한다.
        long owner = UserId();
        await using var host = await HostAsync();
        var created = await CreateAsync(host, owner, "close race");
        long room = created.GetProperty("id").GetInt64();
        await using var closing = await fixture.Contexts.CreateDbContextAsync();
        await using var transaction = await closing.Database.BeginTransactionAsync();
        var locked = (await closing.OpenChatRooms.FromSqlInterpolated(
            $"SELECT * FROM open_chat_room WHERE chat_room_id = {room} FOR UPDATE").ToListAsync()).Single();

        // 실행: HTTP update가 아직 완료되지 않는 동안 다른 연결에서 CLOSED를 commit한다.
        var pending = SendAsync(host, owner, HttpMethod.Patch, $"/api/v1/chat/open-rooms/{room}/me/profile", "{\"nickname\":\"late change\"}");
        await Task.Delay(150);
        Assert.False(pending.IsCompleted);
        locked.Status = "CLOSED";
        await closing.SaveChangesAsync();
        await transaction.CommitAsync();
        using var response = await pending;

        // 검증: lock 대기 이전의 낡은 상태로 수정하지 않는다.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("OPEN_CHAT_ROOM_CLOSED", (await BodyAsync(response)).GetProperty("errorCode").GetString());
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal("nickname", (await read.OpenChatMemberProfiles.SingleAsync(profile =>
            profile.ChatRoomMemberId == created.GetProperty("myOpenProfile").GetProperty("openChatMemberId").GetInt64())).Nickname);
    }

    private Task<ChatRuntimeTestHost> HostAsync(string? prefix = null, bool accountConfigured = true, SyntheticStorage? storage = null)
    {
        return ChatRuntimeTestHost.StartAsync(fixture, prefix ?? Prefix(), configure: services =>
        {
            // 운영 AddChatRuntime/AddChatDatabase의 OPEN 등록을 그대로 사용한다.
            if (accountConfigured)
            {
                services.AddSingleton<IChatRoomAccountReader, SyntheticAccounts>();
            }

            if (storage is not null)
            {
                services.AddSingleton<IOpenProfileStorage>(storage);
            }
        });
    }

    private static long UserId()
    {
        return Random.Shared.NextInt64(10000000, 20000000);
    }

    private static string Prefix()
    {
        return "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID") + ":open:" + Guid.NewGuid().ToString("N");
    }

    private static async Task<JsonElement> CreateAsync(ChatRuntimeTestHost host, long user, string name, string visibility = "PUBLIC")
    {
        using var response = await SendAsync(host, user, HttpMethod.Post, "/api/v1/chat/open-rooms",
            JsonSerializer.Serialize(new
            {
                name,
                description = " description ",
                visibility,
                ownerProfile = new
                {
                    nickname = " nickname "
                }
            }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await BodyAsync(response);
    }

    private static async Task<JsonElement> GetAsync(ChatRuntimeTestHost host, long user, string path)
    {
        using var response = await SendAsync(host, user, HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await BodyAsync(response);
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
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("body").Clone();
    }

    private sealed class SyntheticAccounts : IChatRoomAccountReader
    {
        public Task EnsureUserExistsAsync(long? userId, CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public Task<string> GetAuditIdentityAsync(long userId, CancellationToken token)
        {
            return Task.FromResult("USER:" + userId);
        }

        public Task<ChatDirectPartner> GetDirectPartnerAsync(long userId, CancellationToken token)
        {
            throw new InvalidOperationException("OPEN must not read a global profile.");
        }
    }

    private sealed class SyntheticStorage(IDbContextFactory<ChatDbContext> contexts) : IOpenProfileStorage
    {
        public bool FailResolve
        {
            get; set;
        }
        public List<string> Deleted { get; } = [];
        public bool ObservedCommittedNull
        {
            get; private set;
        }
        public Task<string?> ResolveUrlAsync(string key, CancellationToken token)
        {
            return FailResolve
            ? Task.FromException<string?>(new OpenRoomDependencyUnavailableException())
            : Task.FromResult<string?>("https://synthetic.invalid/" + key);
        }

        public async Task DeleteAsync(string key, CancellationToken token)
        {
            await using var context = await contexts.CreateDbContextAsync(token);
            ObservedCommittedNull = !await context.OpenChatMemberProfiles.AnyAsync(profile => profile.ProfileImageObjectKey == key, token);
            Deleted.Add(key);
        }
    }
}
