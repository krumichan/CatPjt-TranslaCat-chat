using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class ChatRoomRuntimeTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task CreateDirect_ReusesManualRoomAndDoesNotReuseFriendRoom()
    {
        // 준비
        long owner = Random.Shared.NextInt64(1000000, 2000000);
        long target = owner + 1;
        await using var host = await HostAsync();

        // 실행
        var first = await CreateAsync(host, owner, $"{{\"roomType\":\"DIRECT\",\"name\":\"ignored\",\"memberUserIds\":[\"{target}\",{target}]}}");
        var second = await CreateAsync(host, owner, $"{{\"roomType\":0,\"memberUserIds\":[{target}]}}");

        // 검증
        var roomId = first.GetProperty("id").GetInt64();
        Assert.Equal(roomId, second.GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("name").ValueKind);
        Assert.Equal("MANUAL", first.GetProperty("sourceType").GetString());
        Assert.Equal("OWNER", first.GetProperty("myRole").GetString());
        Assert.Equal(2, first.GetProperty("memberCount").GetInt64());
        Assert.True(first.GetProperty("roomLanguageSettingApplied").GetBoolean());

        // 실행 / 검증 — source type이 다른 DIRECT를 MANUAL 재사용 대상으로 취급하지 않는다.
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            await context.ChatRooms.Where(room => room.Id == roomId).ExecuteUpdateAsync(setters => setters.SetProperty(room => room.SourceType, "FRIEND"));
        }
        var third = await CreateAsync(host, owner, $"{{\"roomType\":\"DIRECT\",\"memberUserIds\":[{target}]}}");
        Assert.NotEqual(roomId, third.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task GroupCreate_PreservesNamesLanguageSnapshotAndAtomicMemberships()
    {
        // 준비
        long owner = Random.Shared.NextInt64(2000001, 3000000);
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            context.UserChatLanguageSettings.Add(new UserChatLanguageSettingEntity
            {
                UserId = owner,
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
        var room = await CreateAsync(host, owner, $"{{\"roomType\":\" GROUP \",\"name\":\"  합성 그룹  \",\"description\":null,\"memberUserIds\":[{owner + 1},{owner + 2},{owner + 1}]}}");

        // 검증
        Assert.Equal("  합성 그룹  ", room.GetProperty("name").GetString());
        Assert.Equal("en", room.GetProperty("originalLanguageCode").GetString());
        Assert.Equal(3, room.GetProperty("memberCount").GetInt64());
        var roomId = room.GetProperty("id").GetInt64();
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        var members = await read.ChatRoomMembers.Where(member => member.ChatRoomId == roomId).ToListAsync();
        Assert.Equal(3, members.Count);
        Assert.False(members.Single(member => member.UserId == owner).ShowOriginal);
        Assert.All(members, member => Assert.Null(member.LastReadMessageId));
    }

    [Theory]
    [InlineData("{\"roomType\":\"OPEN\",\"memberUserIds\":[22]}", 400)]
    [InlineData("{\"roomType\":\"DIRECT\",\"memberUserIds\":[22,23]}", 400)]
    [InlineData("{\"roomType\":\"GROUP\",\"memberUserIds\":[]}", 400)]
    [InlineData("{\"roomType\":\"GROUP\",\"memberUserIds\":[21]}", 400)]
    [InlineData("{\"roomType\":\"DIRECT\",\"memberUserIds\":null}", 500)]
    [InlineData("{\"roomType\":\"UNKNOWN\",\"memberUserIds\":[22]}", 500)]
    public async Task Create_InvalidContractsFailBeforeAnyRoomWrite(string json, int expectedStatus)
    {
        // 준비
        await using var host = await HostAsync();
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        var before = await read.ChatRooms.CountAsync();

        // 실행
        using var request = Request(host, 21, HttpMethod.Post, "/api/v1/chat/rooms", json);
        using var response = await host.Client.SendAsync(request);

        // 검증
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal(before, await read.ChatRooms.CountAsync());
    }

    [Fact]
    public async Task MissingAccountAdapter_FailsClosedBeforeRoomCreation()
    {
        // 준비
        await using var host = await ChatRuntimeTestHost.StartAsync(fixture, Prefix());
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        var before = await read.ChatRooms.CountAsync();

        // 실행
        using var request = Request(host, 99, HttpMethod.Post, "/api/v1/chat/rooms", "{\"roomType\":\"GROUP\",\"memberUserIds\":[100]}");
        using var response = await host.Client.SendAsync(request);

        // 검증
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(before, await read.ChatRooms.CountAsync());
    }

    [Fact]
    public async Task ListAndDetail_FilterInactiveMembershipAndClosedOpenRoomButKeepUnread()
    {
        // 준비
        long owner = Random.Shared.NextInt64(3000001, 4000000);
        var group = await fixture.SeedReadRoomAsync("GROUP", owner);
        var open = await fixture.SeedReadRoomAsync("OPEN", owner);
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            context.OpenChatRooms.Add(new OpenChatRoomEntity
            {
                ChatRoomId = open.RoomId,
                Status = "CLOSED",
                Visibility = "PUBLIC",
                MaxMemberCount = 10,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            await context.SaveChangesAsync();
        }
        await using var host = await HostAsync();

        // 실행
        using var request = Request(host, owner, HttpMethod.Get, "/api/v1/chat/rooms");
        using var response = await host.Client.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // 검증 — 목록은 종료 OPEN을 제외한다. 남은 실제 SENT 타인 메시지 두 개를 집계한다.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var listed = Assert.Single(json.RootElement.GetProperty("body").GetProperty("chatRooms").EnumerateArray());
        Assert.Equal(group.RoomId, listed.GetProperty("id").GetInt64());
        Assert.Equal(2, listed.GetProperty("unreadCount").GetInt64());
        using var denied = Request(host, owner + 88, HttpMethod.Get, $"/api/v1/chat/rooms/{group.RoomId}");
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.SendAsync(denied)).StatusCode);
    }

    private async Task<ChatRuntimeTestHost> HostAsync()
    {
        return await ChatRuntimeTestHost.StartAsync(fixture, Prefix(),
        configure: services => services.AddSingleton<IChatRoomAccountReader, SyntheticAccounts>());
    }

    private static string Prefix()
    {
        return "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID") + ":rooms:" + Guid.NewGuid().ToString("N");
    }

    private static HttpRequestMessage Request(ChatRuntimeTestHost host, long userId, HttpMethod method, string path, string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.CreateToken(userId));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }
    private static async Task<JsonElement> CreateAsync(ChatRuntimeTestHost host, long userId, string json)
    {
        using var request = Request(host, userId, HttpMethod.Post, "/api/v1/chat/rooms", json);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(201, document.RootElement.GetProperty("resultCode").GetInt32());
        return document.RootElement.GetProperty("body").Clone();
    }
    private sealed class SyntheticAccounts : IChatRoomAccountReader
    {
        public Task EnsureUserExistsAsync(long? userId, CancellationToken cancellationToken)
        {
            return userId is null
            ? Task.FromException(new ChatRoomException("사용자를 찾을 수 없습니다.")) : Task.CompletedTask;
        }

        public Task<string> GetAuditIdentityAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult($"USER:{userId}");
        }

        public Task<ChatDirectPartner> GetDirectPartnerAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ChatDirectPartner(userId, "synthetic", "합성 사용자", null, null, null, null));
        }
    }
}
