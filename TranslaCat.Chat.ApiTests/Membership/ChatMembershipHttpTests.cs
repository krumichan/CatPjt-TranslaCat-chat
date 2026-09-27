using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Api.Membership;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Rooms;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Rooms;

namespace TranslaCat.Chat.ApiTests.Membership;

public sealed class ChatMembershipHttpTests
{
    [Theory]
    [InlineData("74", "normal", 200, "")]
    [InlineData("73", "normal", 400, "FRIEND_CHAT_SELF_NOT_ALLOWED")]
    [InlineData("74", "not-friend", 400, "FRIEND_RELATION_REQUIRED")]
    [InlineData("74", "blocked", 400, "USER_BLOCKED_BETWEEN")]
    [InlineData("invalid", "normal", 500, "")]
    public async Task Friend_DIRECT_route_revalidates_permissions_before_reusing_existing_room(string target, string scenario, int status, string code)
    {
        // 준비
        await using var host = await MembershipHttpHost.StartAsync(scenario);

        // 실행
        using var response = await host.SendAsync("/api/v1/chat/friends/" + target + "/direct-room", "");
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(status, (int)response.StatusCode);
        if (status == 200)
        {
            Assert.Equal(91, json.GetProperty("body").GetProperty("id").GetInt64());
            Assert.Equal("DIRECT", json.GetProperty("body").GetProperty("roomType").GetString());
            Assert.Empty(host.Store.Added);
        }
        else
        {
            Assert.Equal(code, json.GetProperty("body").GetProperty("errorCode").GetString());
            Assert.Equal(0, host.Store.Entered);
        }
    }

    [Theory]
    [InlineData("/api/v1/chat/rooms/41/members/invitations", "{\"targetUserIds\":[\"74\",74],\"targetPublicIds\":[\" target \"],\"ignored\":true}", false)]
    [InlineData("/api/v1/chat/rooms/42/group-conversion", "{\"name\":\" new group \",\"targetUserIds\":[74,75]}", true)]
    public async Task Invitation_and_conversion_routes_return_201_and_original_flat_contract(string path, string body, bool created)
    {
        // 준비: 실제 routing/binding/Application을 실행하고 저장·계정만 테스트 구성으로 대체한다.
        await using var host = await MembershipHttpHost.StartAsync();

        // 실행
        using var response = await host.SendAsync(path, body);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(201, json.GetProperty("resultCode").GetInt32());
        Assert.Equal("Created", json.GetProperty("message").GetString());
        var result = json.GetProperty("body");
        Assert.Equal(created, result.GetProperty("createdNewGroupRoom").GetBoolean());
        Assert.Equal(created ? 90 : 41, result.GetProperty("roomId").GetInt64());
        var members = result.GetProperty("invitedMembers").EnumerateArray().ToArray();
        Assert.Equal(created ? 2 : 1, members.Length);
        Assert.Equal(74, members[0].GetProperty("userId").GetInt64());
        Assert.Equal("target", members[0].GetProperty("publicId").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, members[0].GetProperty("displayName").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, members[0].GetProperty("profileImageUrl").ValueKind);
        Assert.Equal("2026-09-20T03:00:00Z", members[0].GetProperty("joinedAt").GetString());
        Assert.Equal(created ? 0 : 1, host.Store.SystemMessages.Count);
    }

    [Theory]
    [InlineData("{}", "CHAT_ROOM_INVITE_TARGET_REQUIRED")]
    [InlineData("{\"targetUserIds\":[null]}", "CHAT_ROOM_INVITE_TARGET_NOT_FOUND")]
    [InlineData("{\"targetUserIds\":[73]}", "CHAT_ROOM_INVITE_SELF_NOT_ALLOWED")]
    [InlineData("{\"targetPublicIds\":[\"missing\"]}", "CHAT_ROOM_INVITE_TARGET_NOT_FOUND")]
    public async Task Semantic_invitation_errors_keep_business_400_and_no_state_change(string body, string code)
    {
        // 준비
        await using var host = await MembershipHttpHost.StartAsync();

        // 실행
        using var response = await host.SendAsync("/api/v1/chat/rooms/41/members/invitations", body);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, json.GetProperty("body").GetProperty("errorCode").GetString());
        Assert.Empty(host.Store.Added);
        Assert.Empty(host.Store.Intents);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("{\"targetUserIds\":74}")]
    [InlineData("{\"targetUserIds\":[9223372036854775808]}")]
    [InlineData("{\"targetPublicIds\":[{}]}")]
    public async Task Invalid_binding_uses_original_generic_500_without_entering_store(string body)
    {
        // 준비
        await using var host = await MembershipHttpHost.StartAsync();

        // 실행
        using var response = await host.SendAsync("/api/v1/chat/rooms/41/members/invitations", body);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("", json.GetProperty("body").GetProperty("errorCode").GetString());
        Assert.Equal(0, host.Store.Entered);
    }

    [Theory]
    [InlineData("not-friend", "FRIEND_RELATION_REQUIRED", 400)]
    [InlineData("blocked", "USER_BLOCKED_BETWEEN", 400)]
    [InlineData("missing-account", "", 500)]
    [InlineData("missing-directory", "CHAT_MEMBERSHIP_UNAVAILABLE", 503)]
    public async Task Friend_group_checks_shared_ports_without_production_fallback(string scenario, string code, int status)
    {
        // 준비
        await using var host = await MembershipHttpHost.StartAsync(scenario);

        // 실행
        using var response = await host.SendAsync("/api/v1/chat/friends/group-rooms", "{\"memberUserIds\":[74]}");
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(code, json.GetProperty("body").GetProperty("errorCode").GetString());
        Assert.Empty(host.Store.Added);
    }

    [Fact]
    public async Task Friend_group_uses_200_room_detail_and_preserves_raw_name_without_invitation_events()
    {
        // 준비
        await using var host = await MembershipHttpHost.StartAsync();

        // 실행
        using var response = await host.SendAsync("/api/v1/chat/friends/group-rooms", "{\"name\":\" raw \",\"memberUserIds\":[74,74]}");
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(200, json.GetProperty("resultCode").GetInt32());
        Assert.Equal(" raw ", json.GetProperty("body").GetProperty("name").GetString());
        Assert.Equal("FRIEND", json.GetProperty("body").GetProperty("sourceType").GetString());
        Assert.Equal(2, host.Store.Added.Count);
        Assert.Empty(host.Store.Intents);
    }

    [Fact]
    public async Task Unauthorized_request_never_enters_membership_service()
    {
        // 준비
        await using var host = await MembershipHttpHost.StartAsync();

        // 실행
        using var response = await host.SendAsync("/api/v1/chat/rooms/41/members/invitations", "{\"targetUserIds\":[74]}", authenticated: false);

        // 검증
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Store.Entered);
    }
}

internal sealed class MembershipHttpHost(WebApplication application, HttpClient client, MembershipHttpStore store) : IAsyncDisposable
{
    public MembershipHttpStore Store { get; } = store;
    public static readonly DateTime Now = new(2026, 9, 20, 3, 0, 0, DateTimeKind.Unspecified);

    public static async Task<MembershipHttpHost> StartAsync(string scenario = "normal")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddChatReadHttp();
        builder.Services.AddSingleton(new ChatReadLocalTime(TimeZoneInfo.Utc));
        builder.Services.AddChatMembershipHttp();
        var store = new MembershipHttpStore();
        builder.Services.AddSingleton<IChatMembershipStore>(store);
        builder.Services.AddSingleton(new ChatMembershipService(store,
            scenario == "missing-directory" ? null : new MembershipHttpDirectory(scenario), () => Now));
        builder.Services.AddSingleton(new ChatRoomService(store, () => Now));
        builder.Services.AddSingleton<ChatRoomContractMapper>();
        builder.Services.AddAuthentication(TestReadAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestReadAuthenticationHandler>(TestReadAuthenticationHandler.SchemeName, _ => { });
        var app = builder.Build();
        app.UseChatReadHttp();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(app, new HttpClient { BaseAddress = new Uri(address) }, store);
    }

    public async Task<HttpResponseMessage> SendAsync(string path, string body, bool authenticated = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (authenticated)
        {
            request.Headers.Add(TestReadAuthenticationHandler.HeaderName, "73");
        }
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
    }

    private sealed class MembershipHttpDirectory(string scenario) : IChatMembershipDirectory
    {
        public Task<ChatMembershipUser?> FindByIdAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult<ChatMembershipUser?>(userId is 73 or 74 or 75 && !(scenario == "missing-account" && userId == 74)
                        ? new(userId, "synthetic@example.invalid", "계정", userId == 74 ? "target" : "public-" + userId, null, null) : null);
        }

        public Task<ChatMembershipUser?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken)
        {
            return FindByIdAsync(publicId == "target" ? 74 : 0, cancellationToken);
        }

        public Task<bool> AreFriendsAsync(long userId, long targetUserId, CancellationToken cancellationToken)
        {
            return Task.FromResult(scenario != "not-friend");
        }

        public Task<bool> IsBlockedBetweenAsync(long userId, long targetUserId, CancellationToken cancellationToken)
        {
            return Task.FromResult(scenario == "blocked");
        }
    }
}

internal sealed class MembershipHttpStore : IChatMembershipStore, IChatMembershipSession, IChatRoomStore
{
    public int Entered
    {
        get; private set;
    }
    public List<ChatMembershipMember> Added { get; } = [];
    public List<ChatMembershipIntent> Intents { get; } = [];
    public List<string> SystemMessages { get; } = [];
    private string? createdName;
    public Task<T> ExecuteAsync<T>(Func<IChatMembershipSession, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        Entered++;
        return work(this, cancellationToken);
    }
    public Task<ChatMembershipRoom> LockRoomAsync(long roomId, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ChatMembershipRoom(roomId, roomId == 42 ? "DIRECT" : "GROUP", roomId == 42 ? "FRIEND" : "MANUAL", "source"));
    }

    public Task<IReadOnlyList<ChatMembershipMember>> GetActiveMembersAsync(long roomId, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<ChatMembershipMember>>(roomId == 42
                ? [new(1, 73, "OWNER", MembershipHttpHost.Now), new(2, 74, "MEMBER", MembershipHttpHost.Now)]
                : [new(1, 73, "OWNER", MembershipHttpHost.Now)]);
    }

    public Task<long?> GetLatestSentMessageIdAsync(long roomId, CancellationToken cancellationToken)
    {
        return Task.FromResult<long?>(12);
    }

    public Task<long?> FindFriendDirectAsync(long userId, long friendUserId, CancellationToken cancellationToken)
    {
        return Task.FromResult<long?>(91);
    }

    public Task<ChatMembershipRoom> CreateFriendDirectAsync(ChatMembershipUser owner, DateTime now, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<ChatMembershipRoom> CreateGroupAsync(string? name, string? description, ChatMembershipUser owner, string sourceType, DateTime now, CancellationToken cancellationToken)
    {
        createdName = name;
        return Task.FromResult(new ChatMembershipRoom(90, "GROUP", sourceType, name));
    }
    public Task<ChatMembershipMember> AddOrRestoreAsync(ChatMembershipRoom room, ChatMembershipUser user, string role,
        long? initialCursor, string auditIdentity, DateTime now, CancellationToken cancellationToken)
    {
        var member = new ChatMembershipMember(Added.Count + 20, user.Id, role, now);
        Added.Add(member);
        return Task.FromResult(member);
    }
    public Task<ChatMessageView> InsertSystemMessageAsync(long roomId, string content, string auditIdentity, DateTime now, CancellationToken cancellationToken)
    {
        SystemMessages.Add(content);
        return Task.FromResult(new ChatMessageView(100, roomId, null, null, null, null, null, "SYSTEM", "SYSTEM", content, "SENT", null, [], now, now, null));
    }
    public void RegisterAfterCommit(ChatMembershipIntent intent)
    {
        Intents.Add(intent);
    }

    public Task<long> CreateOrReuseAsync(long userId, ChatRoomCreateRequest request, IReadOnlyList<long?> distinctMembers, DateTime now, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<ChatRoomView> GetAsync(long userId, long roomId, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ChatRoomView(roomId, roomId == 91 ? "DIRECT" : "GROUP", "FRIEND", createdName, null, userId, 2, true,
                "ko", "ja", true, MembershipHttpHost.Now, MembershipHttpHost.Now, "OWNER", null));
    }

    public Task<IReadOnlyList<ChatRoomListItem>> ListAsync(long userId, CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }
}
