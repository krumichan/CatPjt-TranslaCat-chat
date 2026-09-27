using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TranslaCat.Chat.Api.Runtime;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.Presence;
using TranslaCat.Chat.Application.ProfileImages;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class ChatRuntimePipelineTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task HttpRead_RealEfCommitRedisRelayAndStompAcrossTwoApplicationInstances()
    {
        // 준비 — 별도 Kestrel/DI/multiplexer 두 개가 같은 전용 MySQL/Redis를 사용한다.
        var room = await fixture.SeedReadRoomAsync();
        var prefix = Prefix();
        await using var writer = await ChatRuntimeTestHost.StartAsync(fixture, prefix);
        await using var receiver = await ChatRuntimeTestHost.StartAsync(fixture, prefix);
        using var socket = await receiver.ConnectAsync(room.UserId);
        await socket.SubscribeAsync("self", "/user/queue/chat/read");
        await socket.SubscribeAsync("room", $"/topic/chat/rooms/{room.RoomId}");

        // 실행 — HTTP 요청은 실제 binding/auth/Application/EF transaction/직렬화를 통과한다.
        using var response = await writer.MarkReadAsync(room.UserId, room.RoomId, room.FirstId);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var own = await socket.ReceiveEventAsync("chat.read.updated");
        var member = await socket.ReceiveEventAsync("chat.member.read.updated");

        // 검증 — 다른 app의 socket으로 수신한 시점에 독립 connection에서 commit 상태를 확인한다.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(room.FirstId, own.GetProperty("lastReadMessageId").GetInt64());
        Assert.Equal(1, own.GetProperty("unreadCount").GetInt64());
        Assert.Equal(room.UserId, member.GetProperty("readerUserId").GetInt64());
        Assert.Equal(JsonValueKind.Null, member.GetProperty("readerOpenChatMemberId").ValueKind);
        Assert.Equal(JsonValueKind.Null, member.GetProperty("previousLastReadMessageId").ValueKind);
        await using var persisted = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(room.FirstId, (await persisted.ChatRoomMembers.SingleAsync(value => value.Id == room.MemberId)).LastReadMessageId);
        using var readiness = await writer.Client.GetAsync("/api/ready");
        using var readinessBody = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.Equal("READY", readinessBody.RootElement.GetProperty("read").GetString());
        Assert.Equal("NOT_CONFIGURED", readinessBody.RootElement.GetProperty("messages").GetString());
        Assert.Equal("CONFIGURED_NOT_VERIFIED", readinessBody.RootElement.GetProperty("sourceTimeZone").GetString());
        Assert.Equal("NOT_READY", readinessBody.RootElement.GetProperty("presence").GetString());
        Assert.Equal("NOT_CONFIGURED", readinessBody.RootElement.GetProperty("presenceProfiles").GetString());
        Assert.Equal("NOT_CONFIGURED", readinessBody.RootElement.GetProperty("imageUploads").GetString());
    }

    [Fact]
    public async Task NullableNoOp_RealHttpAndSelfStompPreserveExplicitNull()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            var member = await context.ChatRoomMembers.SingleAsync(value => value.Id == room.MemberId);
            member.LastReadMessageId = room.SecondId;
            member.LastReadAt = null;
            await context.SaveChangesAsync();
        }
        await using var host = await ChatRuntimeTestHost.StartAsync(fixture, Prefix());
        using var socket = await host.ConnectAsync(room.UserId);
        await socket.SubscribeAsync("self", "/user/queue/chat/read");

        // 실행
        using var response = await host.MarkReadAsync(room.UserId, room.RoomId, room.FirstId);
        var text = await response.Content.ReadAsStringAsync();
        var own = await socket.ReceiveEventAsync("chat.read.updated");

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"lastReadAt\":null", text);
        Assert.Equal(JsonValueKind.Null, own.GetProperty("lastReadAt").ValueKind);
        Assert.Equal(room.SecondId, own.GetProperty("lastReadMessageId").GetInt64());
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        Assert.Null((await read.ChatRoomMembers.SingleAsync(value => value.Id == room.MemberId)).LastReadAt);
    }

    [Fact]
    public async Task OpenReadEvent_UsesMembershipIdWhileClosedRoomRejectsRealtimeSubscription()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync("OPEN");
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            context.OpenChatRooms.Add(new OpenChatRoomEntity
            {
                ChatRoomId = room.RoomId,
                Visibility = "PUBLIC",
                Status = "ACTIVE",
                MaxMemberCount = 20,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            await context.SaveChangesAsync();
        }
        await using var host = await ChatRuntimeTestHost.StartAsync(fixture, Prefix());
        using var socket = await host.ConnectAsync(room.UserId);
        await socket.SubscribeAsync("room", $"/topic/chat/rooms/{room.RoomId}");

        // 실행 / 검증 — OPEN 공개 읽음 이벤트에서 일반 사용자 ID를 노출하지 않는다.
        using var response = await host.MarkReadAsync(room.UserId, room.RoomId, room.FirstId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var member = await socket.ReceiveEventAsync("chat.member.read.updated");
        Assert.Equal(JsonValueKind.Null, member.GetProperty("readerUserId").ValueKind);
        Assert.Equal(room.MemberId, member.GetProperty("readerOpenChatMemberId").GetInt64());

        // 실행 / 검증 — 종료 후 새 STOMP 구독은 실패하지만 원본 읽음 자체는 CLOSED를 추가 검사하지 않는다.
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            await context.OpenChatRooms.Where(value => value.ChatRoomId == room.RoomId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Status, "CLOSED"));
        }
        var closure = JsonSerializer.Serialize(new
        {
            eventType = "chat.room.closed",
            roomId = room.RoomId,
            closedAt = "2026-09-26T12:00:00Z",
            occurredAt = "2026-09-26T12:00:00Z"
        });
        await host.Services.GetRequiredService<ChatRealtimeRedisRelay>().PublishRoomClosureAsync(room.RoomId, closure, CancellationToken.None);
        Assert.Equal(room.RoomId, (await socket.ReceiveEventAsync("chat.room.closed")).GetProperty("roomId").GetInt64());
        using var reconnect = await host.ConnectAsync(room.UserId);
        await reconnect.SendAsync($"SUBSCRIBE\nid:closed\ndestination:/topic/chat/rooms/{room.RoomId}\n\n\0");
        Assert.StartsWith("ERROR\n", await reconnect.ReceiveAsync());
        using var closedRead = await host.MarkReadAsync(room.UserId, room.RoomId, room.SecondId);
        Assert.Equal(HttpStatusCode.OK, closedRead.StatusCode);
    }

    [Fact]
    public async Task MissingIdentity_IsNotReadyAndSignedTokenCannotBypassAuthentication()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        await using var host = await ChatRuntimeTestHost.StartAsync(fixture, Prefix(), identityConfigured: false);

        // 실행
        using var ready = await host.Client.GetAsync("/api/ready");
        using var response = await host.MarkReadAsync(room.UserId, room.RoomId, room.FirstId);
        using var health = await host.Client.GetAsync("/api/health");

        // 검증
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains("NOT_CONFIGURED", await ready.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MissingDatabaseOrRedis_FailsClosedWithoutProductionFake(bool database, bool redis)
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        await using var host = await ChatRuntimeTestHost.StartAsync(fixture, Prefix(), databaseConfigured: database, redisConfigured: redis);

        // 실행
        using var ready = await host.Client.GetAsync("/api/ready");
        using var response = await host.MarkReadAsync(room.UserId, room.RoomId, room.FirstId);

        // 검증
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        Assert.Null((await read.ChatRoomMembers.SingleAsync(value => value.Id == room.MemberId)).LastReadMessageId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Readiness_RequiresActualStorageUploadCapabilityAndPresenceIdentity(bool completeStorage)
    {
        // 준비 — URL 전용 storage와 업로드 capability는 테스트 구성에서만 서로 다르게 등록한다.
        await using var host = await ChatRuntimeTestHost.StartAsync(fixture, Prefix(), configure: services =>
        {
            IOpenProfileStorage storage = completeStorage ? new UploadStorage() : new UrlStorage();
            services.AddSingleton(storage);
            services.AddSingleton((IChatAiProfileStorage)storage);
            services.AddSingleton<IChatPresenceProfileReader, PresenceProfiles>();

            // 별도 capability 등록만으로 실제 업무가 사용하는 URL storage를 대체할 수는 없다.
            services.AddSingleton<IChatProfileImageObjectStore, UploadStorage>();
        });

        // 실행
        using var response = await host.Client.GetAsync("/api/ready");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // 검증 — 외부 연결을 실행하지 않았으므로 구성 확인과 실제 가용성 검증을 구분한다.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("CONFIGURED_NOT_PROBED", json.RootElement.GetProperty("openStorage").GetString());
        Assert.Equal("CONFIGURED_NOT_PROBED", json.RootElement.GetProperty("aiStorage").GetString());
        Assert.Equal(completeStorage ? "CONFIGURED_NOT_PROBED" : "NOT_CONFIGURED",
            json.RootElement.GetProperty("imageUploads").GetString());
        Assert.Equal("READY", json.RootElement.GetProperty("presence").GetString());
        Assert.Equal("CONFIGURED_NOT_PROBED", json.RootElement.GetProperty("presenceProfiles").GetString());
    }

    private class UrlStorage : IOpenProfileStorage, IChatAiProfileStorage
    {
        public Task<string?> ResolveUrlAsync(string key, CancellationToken token)
        {
            throw new InvalidOperationException();
        }

        public Task DeleteAsync(string key, CancellationToken token)
        {
            throw new InvalidOperationException();
        }
    }

    private sealed class UploadStorage : UrlStorage, IChatProfileImageObjectStore
    {
        public Task StoreAsync(string key, string type, ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            throw new InvalidOperationException();
        }
    }

    private sealed class PresenceProfiles : IChatPresenceProfileReader
    {
        public Task<string?> FindPublicIdAsync(long userId, CancellationToken token)
        {
            throw new InvalidOperationException();
        }
    }

    private static string Prefix()
    {
        return "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID") + ":pipeline:" + Guid.NewGuid().ToString("N");
    }
}
