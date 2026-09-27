using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.MembershipQuery;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class MemberQueryRuntimeTests(ChatMySqlFixture fixture)
{
    [Theory]
    [InlineData("PUBLIC", true)]
    [InlineData("PRIVATE", false)]
    public async Task List_uses_actual_membership_and_Redis_presence_with_safe_AI_fields(string disclosure, bool visible)
    {
        // 준비: 프로필/스토리지 port만 합성이며 권한·DB 조회·Presence·HTTP 직렬화는 실제 구현이다.
        var room = await SeedAsync(disclosure: disclosure, withAi: true);
        await using var host = await HostAsync(new Profiles(), storage: true);
        using var socket = await host.ConnectAsync(room.UserId);

        // 실행
        using var response = await GetAsync(host, room.UserId, $"/api/v1/chat/rooms/{room.RoomId}/members");
        var body = await BodyAsync(response);

        // 검증: inactive/deleted 및 비활성 AI agent는 제외하고 joinedAt 순서·nullable를 보존한다.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var members = body.GetProperty("members").EnumerateArray().ToArray();
        Assert.Equal(2, members.Length);
        Assert.Equal(room.UserId, members[0].GetProperty("userId").GetInt64());
        Assert.Equal(JsonValueKind.Null, members[0].GetProperty("displayName").ValueKind);
        Assert.Equal(JsonValueKind.Null, members[0].GetProperty("leftAt").ValueKind);
        Assert.False(members[0].TryGetProperty("email", out _));
        Assert.Equal("2026-09-26T12:00:00Z", members[0].GetProperty("joinedAt").GetString());
        if (visible)
        {
            Assert.True(members[0].GetProperty("online").GetBoolean());
            Assert.False(members[1].GetProperty("online").GetBoolean());
        }
        else
        {
            Assert.All(members, member => Assert.Equal(JsonValueKind.Null, member.GetProperty("online").ValueKind));
        }
        var ai = Assert.Single(body.GetProperty("aiMembers").EnumerateArray());
        Assert.Equal("표시용 AI", ai.GetProperty("nickname").GetString());
        Assert.Equal("https://synthetic.example.invalid/ai.png", ai.GetProperty("profileImageUrl").GetString());
        Assert.Equal(6, ai.EnumerateObject().Count());
        Assert.False(ai.TryGetProperty("personaPrompt", out _));
        Assert.False(ai.TryGetProperty("aiAgentId", out _));
        Assert.Equal(disclosure, body.GetProperty("aiDisclosureType").GetString());
    }

    [Theory]
    [InlineData("BLOCKED")]
    [InlineData("FRIEND")]
    [InlineData("REQUEST_SENT")]
    [InlineData("REQUEST_RECEIVED")]
    [InlineData("NONE")]
    public async Task Profile_preserves_shared_relationship_status_and_nullable_public_fields(string status)
    {
        // 준비
        var room = await SeedAsync();
        var profiles = new Profiles(status);
        await using var host = await HostAsync(profiles);

        // 실행
        using var response = await GetAsync(host, room.UserId, $"/api/v1/chat/rooms/{room.RoomId}/members/{room.UserId + 1}/profile");
        var body = await BodyAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(status, body.GetProperty("friendStatus").GetString());
        Assert.Equal(room.UserId + 1, body.GetProperty("userId").GetInt64());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("profileBackgroundImageUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("bio").ValueKind);
        Assert.Equal(8, body.EnumerateObject().Count());
        Assert.Equal(1, profiles.RelationshipCalls);
    }

    [Fact]
    public async Task Self_profile_short_circuits_shared_relationship_and_empty_AI_list_has_null_disclosure()
    {
        // 준비
        var room = await SeedAsync(disclosure: "PRIVATE");
        var profiles = new Profiles("invalid-if-called");
        await using var host = await HostAsync(profiles);

        // 실행
        using var self = await GetAsync(host, room.UserId, $"/api/v1/chat/rooms/{room.RoomId}/members/{room.UserId}/profile");
        using var list = await GetAsync(host, room.UserId, $"/api/v1/chat/rooms/{room.RoomId}/members");
        var selfBody = await BodyAsync(self);
        var listBody = await BodyAsync(list);

        // 검증
        Assert.Equal(HttpStatusCode.OK, self.StatusCode);
        Assert.Equal("SELF", selfBody.GetProperty("friendStatus").GetString());
        Assert.Equal(0, profiles.RelationshipCalls);
        Assert.Empty(listBody.GetProperty("aiMembers").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, listBody.GetProperty("aiDisclosureType").ValueKind);
        Assert.Equal(JsonValueKind.Null, selfBody.GetProperty("online").ValueKind);
    }

    [Theory]
    [InlineData("outsider", "CHAT_ROOM_MEMBER_ACCESS_DENIED", 400)]
    [InlineData("inactive-target", "CHAT_ROOM_MEMBER_ACCESS_DENIED", 400)]
    [InlineData("open", "OPEN_CHAT_MEMBER_PROFILE_API_REQUIRED", 400)]
    [InlineData("missing-profile", "CHAT_MEMBERSHIP_UNAVAILABLE", 503)]
    [InlineData("missing-storage", "CHAT_MEMBERSHIP_UNAVAILABLE", 503)]
    public async Task Source_access_rules_and_missing_shared_adapters_fail_closed(string scenario, string code, int status)
    {
        // 준비
        var room = await SeedAsync(roomType: scenario == "open" ? "OPEN" : "GROUP", withAi: scenario == "missing-storage");
        var profiles = new Profiles();
        await using var host = await HostAsync(scenario == "missing-profile" ? null : profiles);
        var path = scenario == "inactive-target"
            ? $"/api/v1/chat/rooms/{room.RoomId}/members/{room.UserId + 2}/profile" : $"/api/v1/chat/rooms/{room.RoomId}/members";

        // 실행
        using var response = await GetAsync(host, scenario == "outsider" ? room.UserId + 8 : room.UserId, path);
        var body = await BodyAsync(response);

        // 검증
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(code, body.GetProperty("errorCode").GetString());
        if (scenario is "outsider" or "inactive-target" or "open")
        {
            Assert.Equal(0, profiles.ProfileCalls);
        }
    }

    private async Task<SeededReadRoom> SeedAsync(string roomType = "GROUP", string? disclosure = null, bool withAi = false)
    {
        var room = await fixture.SeedReadRoomAsync(roomType, Random.Shared.NextInt64(8_000_000_000, 9_000_000_000));
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        for (var index = 1; index <= 3; index++)
        {
            context.ChatRoomMembers.Add(new ChatRoomMemberEntity
            {
                ChatRoomId = room.RoomId,
                UserId = room.UserId + index,
                Role = "MEMBER",
                Active = index != 2,
                DeletedAt = index == 3 ? ChatMySqlFixture.Epoch : null,
                JoinedAt = ChatMySqlFixture.Epoch.AddSeconds(index),
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
        }
        if (disclosure is not null)
        {
            context.ChatRoomAiSettings.Add(new ChatRoomAiSettingEntity
            {
                ChatRoomId = room.RoomId,
                DisclosureType = disclosure,
                MentionPermission = "ALL_MEMBERS",
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
        }
        if (withAi)
        {
            foreach (var active in new[] { true, false })
            {
                var agent = new ChatAiAgentEntity
                {
                    Nickname = "표시용 AI",
                    OriginalLanguageCode = "ko",
                    PersonaPrompt = "합성 비공개 persona",
                    Active = active,
                    ProfileImageObjectKey = "synthetic/ai.png",
                    CreatedAt = ChatMySqlFixture.Epoch,
                    UpdatedAt = ChatMySqlFixture.Epoch
                };
                context.ChatAiAgents.Add(agent);
                await context.SaveChangesAsync();
                context.ChatRoomAiMembers.Add(new ChatRoomAiMemberEntity
                {
                    ChatRoomId = room.RoomId,
                    AiAgentId = agent.Id,
                    JoinedAt = ChatMySqlFixture.Epoch,
                    CreatedAt = ChatMySqlFixture.Epoch,
                    UpdatedAt = ChatMySqlFixture.Epoch
                });
            }
        }
        await context.SaveChangesAsync();
        return room;
    }

    private Task<ChatRuntimeTestHost> HostAsync(Profiles? profiles, bool storage = false)
    {
        return ChatRuntimeTestHost.StartAsync(fixture,
                "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID") + ":member-query:" + Guid.NewGuid().ToString("N"),
                configure: services =>
                {
                    if (profiles is not null)
                    {
                        services.AddSingleton<IChatMemberProfileReader>(profiles);
                    }
                    if (storage)
                    {
                        services.AddSingleton<IChatAiProfileStorage, ProfileStorage>();
                    }
                });
    }

    private static async Task<HttpResponseMessage> GetAsync(ChatRuntimeTestHost host, long userId, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.CreateToken(userId));
        return await host.Client.SendAsync(request);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("body").Clone();
    }

    private sealed class Profiles(string relationship = "FRIEND") : IChatMemberProfileReader
    {
        public int RelationshipCalls
        {
            get; private set;
        }
        public int ProfileCalls
        {
            get; private set;
        }
        public Task<ChatMemberProfileData> GetSummaryAsync(long userId, CancellationToken cancellationToken)
        {
            ProfileCalls++;
            return Task.FromResult(new ChatMemberProfileData(userId, "synthetic-" + userId, null, null, null, null));
        }
        public Task<string> GetFriendStatusAsync(long requesterUserId, long targetUserId, CancellationToken cancellationToken)
        {
            RelationshipCalls++;
            return Task.FromResult(relationship);
        }
    }

    private sealed class ProfileStorage : IChatAiProfileStorage
    {
        public Task<string?> ResolveUrlAsync(string objectKey, CancellationToken token)
        {
            return Task.FromResult<string?>("https://synthetic.example.invalid/ai.png");
        }
    }
}
