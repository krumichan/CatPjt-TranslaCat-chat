using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.ProfileImages;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class ProfileImageRuntimeTests(ChatMySqlFixture fixture)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    [Fact]
    public async Task OPEN_multipart_replaces_object_after_commit_and_preserves_existing_DELETE_route()
    {
        // 준비: storage만 메모리 합성 bytes 대역이다. HTTP/form parser/인증/DB/Redis/STOMP는 실제 경로다.
        var target = await SeedAsync(true);
        var objects = new MemoryObjects(fixture, target.OldKey);
        await using var host = await HostAsync(objects);
        using var socket = await host.ConnectAsync(target.UserId);
        await socket.SubscribeAsync("room", $"/topic/chat/rooms/{target.RoomId}");

        // 실행
        using var uploaded = await UploadAsync(host, target, Png, "image/png");
        var body = await BodyAsync(uploaded);
        var updated = await socket.ReceiveEventAsync("chat.open-profile.updated");
        var newKey = Assert.Single(objects.Stored.Keys);

        // 검증: 원본 key prefix와 UUID·정규화 MIME을 보존하고 old 삭제 시 실제 DB가 new를 참조한다.
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        Assert.Matches($"^open-chat-profiles/{target.MemberId}/[0-9a-f-]{{36}}\\.png$", newKey);
        Assert.Equal("image/png", objects.Stored[newKey].Type);
        Assert.Equal(Png, objects.Stored[newKey].Bytes);
        Assert.Contains(target.OldKey, objects.Deleted);
        Assert.Equal("https://synthetic.example.invalid/" + newKey, body.GetProperty("profileImageUrl").GetString());
        Assert.Equal(target.MemberId, updated.GetProperty("openChatMemberId").GetInt64());
        Assert.Equal(newKey, await CurrentKeyAsync(target));

        // 실행 / 검증: 기존 DELETE도 commit 뒤 object를 지우고 nullable URL을 반환한다.
        using var deleted = await DeleteAsync(host, target);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await BodyAsync(deleted)).GetProperty("profileImageUrl").ValueKind);
        Assert.Null(await CurrentKeyAsync(target));
        Assert.Contains(newKey, objects.Deleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AI_profile_and_background_multipart_preserve_response_and_after_commit_cleanup(bool background)
    {
        // 준비
        var target = await SeedAsync(false, background);
        var objects = new MemoryObjects(fixture, target.OldKey);
        await using var host = await HostAsync(objects);
        using var socket = await host.ConnectAsync(target.UserId);
        await socket.SubscribeAsync("room", $"/topic/chat/rooms/{target.RoomId}");

        // 실행
        using var uploaded = await UploadAsync(host, target, Png, "application/octet-stream");
        var changed = await socket.ReceiveEventAsync("chat.members.changed");
        var body = await BodyAsync(uploaded);
        var newKey = Assert.Single(objects.Stored.Keys);

        // 검증
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        Assert.Matches($"^chat-ai/{target.MemberId}/{(background ? "background" : "profile")}/[0-9a-f-]{{36}}\\.png$", newKey);
        Assert.Equal(target.RoomId, changed.GetProperty("roomId").GetInt64());
        Assert.Equal(target.MemberId, body.GetProperty("aiMemberId").GetInt64());
        Assert.Contains(target.OldKey, objects.Deleted);
        Assert.Equal(newKey, await CurrentKeyAsync(target));

        // 실행 / 검증
        using var deleted = await DeleteAsync(host, target);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Null(await CurrentKeyAsync(target));
        Assert.Contains(newKey, objects.Deleted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Old_object_cleanup_failure_does_not_undo_commit_or_suppress_event(bool open)
    {
        // 준비
        var target = await SeedAsync(open);
        var objects = new MemoryObjects(fixture, target.OldKey) { FailDelete = true };
        await using var host = await HostAsync(objects);
        using var socket = await host.ConnectAsync(target.UserId);
        await socket.SubscribeAsync("room", $"/topic/chat/rooms/{target.RoomId}");

        // 실행
        using var response = await UploadAsync(host, target, Png, "image/png");
        var changed = await socket.ReceiveEventAsync(open ? "chat.open-profile.updated" : "chat.members.changed");

        // 검증: 정리 실패는 별도 재조정 대상이며 commit된 새 참조와 이벤트 전달은 유지한다.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(target.RoomId, changed.GetProperty("roomId").GetInt64());
        Assert.Equal(1, objects.DeleteAttempts);
        Assert.Empty(objects.Deleted);
        Assert.Equal(Assert.Single(objects.Stored.Keys), await CurrentKeyAsync(target));
    }

    [Theory]
    [InlineData(true, "projection")]
    [InlineData(false, "projection")]
    [InlineData(true, "store")]
    [InlineData(false, "store")]
    public async Task Confirmed_rollback_deletes_only_new_object_and_preserves_old_database_reference(bool open, string failure)
    {
        // 준비
        var target = await SeedAsync(open);
        var objects = new MemoryObjects(fixture, target.OldKey) { FailProjection = failure == "projection", FailStore = failure == "store" };
        await using var host = await HostAsync(objects);

        // 실행: store의 부분 성공 또는 DB flush 뒤 URL projection 오류를 재현한다.
        using var response = await UploadAsync(host, target, Png, "image/png");

        // 검증
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(target.OldKey, await CurrentKeyAsync(target));
        Assert.DoesNotContain(target.OldKey, objects.Deleted);
        Assert.Single(objects.Deleted);
        Assert.Empty(objects.Stored);
    }

    [Theory]
    [InlineData("empty", "PROFILE_IMAGE_FILE_REQUIRED", 400)]
    [InlineData("invalid", "PROFILE_IMAGE_INVALID_BINARY", 400)]
    [InlineData("mismatch", "PROFILE_IMAGE_CONTENT_TYPE_MISMATCH", 400)]
    [InlineData("unsupported", "PROFILE_IMAGE_UNSUPPORTED_CONTENT_TYPE", 400)]
    [InlineData("profile-size", "PROFILE_IMAGE_FILE_TOO_LARGE", 400)]
    [InlineData("multipart-size", "", 500)]
    [InlineData("missing-part", "", 500)]
    public async Task Multipart_binding_and_source_image_validation_are_distinct(string scenario, string code, int status)
    {
        // 준비
        var target = await SeedAsync(true);
        var objects = new MemoryObjects(fixture, target.OldKey);
        await using var host = await HostAsync(objects);
        byte[] bytes = scenario switch
        {
            "empty" => [],
            "invalid" => [1, 2, 3],
            "profile-size" => PaddedPng((5 * 1024 * 1024) + 1),
            "multipart-size" => PaddedPng((10 * 1024 * 1024) + 1),
            _ => Png
        };
        var type = scenario == "mismatch" ? "image/jpeg" : scenario == "unsupported" ? "image/gif" : "image/png";

        // 실행
        using var response = await UploadAsync(host, target, bytes, type, scenario == "missing-part" ? "wrong" : "file");

        // 검증: 새 object 저장까지 진행하지 않으며 원본 HTTP generic/business 분기를 보존한다.
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(code, (await BodyAsync(response)).GetProperty("errorCode").GetString());
        Assert.Empty(objects.Stored);
        Assert.Empty(objects.Deleted);
        Assert.Equal(target.OldKey, await CurrentKeyAsync(target));
    }

    [Theory]
    [InlineData("OPEN", "outsider", "OPEN_CHAT_MEMBER_ACCESS_DENIED", 400)]
    [InlineData("OPEN", "closed", "OPEN_CHAT_ROOM_CLOSED", 400)]
    [InlineData("AI", "member", "CHAT_AI_ROOM_MANAGEMENT_ACCESS_DENIED", 400)]
    [InlineData("AI", "missing-storage", "CHAT_PROFILE_IMAGE_UNAVAILABLE", 503)]
    [InlineData("OPEN", "missing-storage", "CHAT_PROFILE_IMAGE_UNAVAILABLE", 503)]
    public async Task Authority_and_upload_capability_are_required_before_object_storage(string kind, string scenario, string code, int status)
    {
        // 준비
        var target = await SeedAsync(kind == "OPEN");
        if (scenario is "closed" or "member")
        {
            await using var context = await fixture.Contexts.CreateDbContextAsync();
            if (scenario == "closed")
            {
                (await context.OpenChatRooms.SingleAsync(row => row.ChatRoomId == target.RoomId)).Status = "CLOSED";
            }
            else
            {
                (await context.ChatRoomMembers.SingleAsync(row => row.ChatRoomId == target.RoomId && row.UserId == target.UserId)).Role = "MEMBER";
            }
            await context.SaveChangesAsync();
        }
        var objects = new MemoryObjects(fixture, target.OldKey);
        await using var host = await HostAsync(scenario == "missing-storage" ? null : objects);

        // 실행
        using var response = await UploadAsync(host, scenario == "outsider" ? target with
        {
            UserId = target.UserId + 1
        } : target, Png, "image/png");

        // 검증
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(code, (await BodyAsync(response)).GetProperty("errorCode").GetString());
        Assert.Empty(objects.Stored);
        Assert.Equal(target.OldKey, await CurrentKeyAsync(target));
    }

    private async Task<ImageTarget> SeedAsync(bool open, bool background = false)
    {
        var room = await fixture.SeedReadRoomAsync(open ? "OPEN" : "GROUP", Random.Shared.NextInt64(10_000_000_000, 11_000_000_000));
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        (await db.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId)).Role = "OWNER";
        string oldKey = "synthetic-old/" + Guid.NewGuid().ToString("N") + ".png";
        long memberId = room.MemberId;
        long agentId = 0;
        if (open)
        {
            db.OpenChatRooms.Add(new OpenChatRoomEntity
            {
                ChatRoomId = room.RoomId,
                Visibility = "PUBLIC",
                MaxMemberCount = 10,
                Status = "ACTIVE",
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            db.OpenChatMemberProfiles.Add(new OpenChatMemberProfileEntity
            {
                ChatRoomMemberId = memberId,
                MemberCode = Guid.NewGuid().ToString("N")[..8],
                Nickname = "합성 프로필",
                ProfileImageObjectKey = oldKey,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
        }
        else
        {
            var agent = new ChatAiAgentEntity
            {
                Nickname = "합성 AI",
                OriginalLanguageCode = "ko",
                PersonaPrompt = "합성 persona",
                ProfileImageObjectKey = background ? null : oldKey,
                ProfileBackgroundImageObjectKey = background ? oldKey : null,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            };
            db.ChatAiAgents.Add(agent);
            await db.SaveChangesAsync();
            agentId = agent.Id;
            var member = new ChatRoomAiMemberEntity
            {
                ChatRoomId = room.RoomId,
                AiAgentId = agent.Id,
                JoinedAt = ChatMySqlFixture.Epoch,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            };
            db.ChatRoomAiMembers.Add(member);
            await db.SaveChangesAsync();
            memberId = member.Id;
        }
        await db.SaveChangesAsync();
        return new(room.UserId, room.RoomId, memberId, agentId, open, background, oldKey);
    }

    private Task<ChatRuntimeTestHost> HostAsync(MemoryObjects? objects)
    {
        return ChatRuntimeTestHost.StartAsync(fixture,
                "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID") + ":images:" + Guid.NewGuid().ToString("N"),
                configure: services =>
                {
                    services.AddSingleton<IChatRoomAccountReader, SyntheticAccounts>();
                    if (objects is not null)
                    {
                        services.AddSingleton<IChatAiProfileStorage>(objects);
                        services.AddSingleton<IOpenProfileStorage>(objects);
                    }
                });
    }

    private async Task<string?> CurrentKeyAsync(ImageTarget target)
    {
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        if (target.Open)
        {
            return await db.OpenChatMemberProfiles.Where(row => row.ChatRoomMemberId == target.MemberId).Select(row => row.ProfileImageObjectKey).SingleAsync();
        }
        var agent = await db.ChatAiAgents.SingleAsync(row => row.Id == target.AgentId);
        return target.Background ? agent.ProfileBackgroundImageObjectKey : agent.ProfileImageObjectKey;
    }

    private static async Task<HttpResponseMessage> UploadAsync(ChatRuntimeTestHost host, ImageTarget target, byte[] bytes, string type, string partName = "file")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, target.Path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.CreateToken(target.UserId));
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        form.Add(file, partName, "synthetic.png");
        request.Content = form;
        return await host.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(ChatRuntimeTestHost host, ImageTarget target)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, target.Path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.CreateToken(target.UserId));
        return await host.Client.SendAsync(request);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("body").Clone();
    }
    private static byte[] PaddedPng(int length)
    {
        var bytes = new byte[length];
        Png.CopyTo(bytes, 0);
        return bytes;
    }
    private sealed record ImageTarget(long UserId, long RoomId, long MemberId, long AgentId, bool Open, bool Background, string OldKey)
    {
        public string Path => Open ? $"/api/v1/chat/open-rooms/{RoomId}/me/profile-image"
            : $"/api/v1/chat/rooms/{RoomId}/ai-members/{MemberId}/{(Background ? "profile-background-image" : "profile-image")}";
    }

    private sealed class MemoryObjects(ChatMySqlFixture fixture, string oldKey) : IChatProfileImageObjectStore, IChatAiProfileStorage, IOpenProfileStorage
    {
        public ConcurrentDictionary<string, (string Type, byte[] Bytes)> Stored { get; } = new();
        public List<string> Deleted { get; } = [];
        public bool FailProjection
        {
            get; init;
        }
        public bool FailStore
        {
            get; init;
        }
        public bool FailDelete
        {
            get; init;
        }
        public int DeleteAttempts
        {
            get; private set;
        }
        public Task StoreAsync(string objectKey, string contentType, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            Stored[objectKey] = (contentType, bytes.ToArray());
            return FailStore ? Task.FromException(new IOException("Synthetic partial storage failure.")) : Task.CompletedTask;
        }
        public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
        {
            // 독립 connection에서 삭제 대상이 DB에 더 이상 참조되지 않을 때만 cleanup이 호출되어야 한다.
            await using var db = await fixture.Contexts.CreateDbContextAsync(cancellationToken);
            Assert.False(await db.OpenChatMemberProfiles.AnyAsync(row => row.ProfileImageObjectKey == objectKey, cancellationToken));
            Assert.False(await db.ChatAiAgents.AnyAsync(row => row.ProfileImageObjectKey == objectKey || row.ProfileBackgroundImageObjectKey == objectKey, cancellationToken));
            DeleteAttempts++;
            if (FailDelete)
            {
                throw new IOException("Synthetic cleanup failure.");
            }
            Deleted.Add(objectKey);
            Stored.TryRemove(objectKey, out _);
        }
        public Task<string?> ResolveUrlAsync(string objectKey, CancellationToken token)
        {
            return FailProjection && objectKey != oldKey ? Task.FromException<string?>(new IOException("Synthetic URL projection failure."))
                        : Task.FromResult<string?>("https://synthetic.example.invalid/" + objectKey);
        }
    }

    private sealed class SyntheticAccounts : IChatRoomAccountReader
    {
        public Task EnsureUserExistsAsync(long? userId, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<string> GetAuditIdentityAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult("synthetic-image@example.invalid");
        }

        public Task<ChatDirectPartner> GetDirectPartnerAsync(long userId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
