using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class ChatMessageRuntimeTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task DirectMessage_HttpAndStompSendUseRealEfRedisAndCrossInstanceDelivery()
    {
        // 준비 — 같은 언어의 DIRECT 방은 번역/AI 호출 대상이 아니다. 계정 프로필만 테스트 대역이다.
        var room = await fixture.SeedReadRoomAsync("DIRECT");
        var otherId = room.UserId + 1;
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            await context.ChatRoomMembers.Where(member => member.Id == room.MemberId).ExecuteUpdateAsync(setters => setters
                .SetProperty(member => member.OriginalLanguageCode, "ko").SetProperty(member => member.TranslationLanguageCode, "ko"));
            context.ChatRoomMembers.Add(new ChatRoomMemberEntity
            {
                ChatRoomId = room.RoomId,
                UserId = otherId,
                Role = "MEMBER",
                JoinedAt = ChatMySqlFixture.Epoch,
                OriginalLanguageCode = "ko",
                TranslationLanguageCode = "ko",
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            await context.SaveChangesAsync();
        }
        var prefix = "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID") + ":message:" + Guid.NewGuid().ToString("N");
        await using var sender = await ChatRuntimeTestHost.StartAsync(fixture, prefix, configure: Profiles);
        await using var receiver = await ChatRuntimeTestHost.StartAsync(fixture, prefix, configure: Profiles);
        using var socket = await receiver.ConnectAsync(otherId);
        await socket.SubscribeAsync("room", $"/topic/chat/rooms/{room.RoomId}");

        // 실행 — REST 작성 후 다른 app의 Redis subscription에서 native STOMP 이벤트를 받는다.
        using var request = Request(sender, room.UserId, HttpMethod.Post, $"/api/v1/chat/rooms/{room.RoomId}/messages", "{\"content\":\"  합성 메시지 😺  \"}");
        using var response = await sender.Client.SendAsync(request);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var createdEvent = await socket.ReceiveEventAsync("chat.message.created");
        var created = createdEvent.GetProperty("message");
        var messageId = created.GetProperty("id").GetInt64();

        // 검증 — 원본의 HTTP200/envelope201과 실제 저장 본문·unreadMemberCount를 확인한다.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(201, payload.RootElement.GetProperty("resultCode").GetInt32());
        Assert.Equal("합성 메시지 😺", created.GetProperty("content").GetString());
        Assert.Equal(1, created.GetProperty("unreadMemberCount").GetInt64());
        Assert.Empty(created.GetProperty("translations").EnumerateArray());
        await using (var read = await fixture.Contexts.CreateDbContextAsync())
        {
            Assert.Equal("합성 메시지 😺", (await read.ChatMessages.SingleAsync(message => message.Id == messageId)).Content);
            Assert.Equal(0, await read.ChatMessageTranslations.CountAsync(translation => translation.ChatMessageId == messageId));
        }

        // 실행 / 검증 — STOMP SEND도 동일 service/EF transaction 경로를 한 번만 실행한다.
        using var senderSocket = await sender.ConnectAsync(room.UserId);
        await senderSocket.SendAsync($"SEND\ndestination:/app/chat/rooms/{room.RoomId}/messages\n\n{{\"content\":\"STOMP 합성\"}}\0");
        var second = (await socket.ReceiveEventAsync("chat.message.created")).GetProperty("message");
        Assert.Equal("STOMP 합성", second.GetProperty("content").GetString());
        using var historyRequest = Request(receiver, otherId, HttpMethod.Get, $"/api/v1/chat/rooms/{room.RoomId}/messages/after?cursorId={messageId}&size=1");
        using var history = await receiver.Client.SendAsync(historyRequest);
        using var historyBody = JsonDocument.Parse(await history.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Equal(second.GetProperty("id").GetInt64(), Assert.Single(historyBody.RootElement.GetProperty("body").GetProperty("messages").EnumerateArray()).GetProperty("id").GetInt64());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingProfileOrRequiredWorkflow_DoesNotLeavePartialMessage(bool profileConfigured)
    {
        // 준비 — 기본 언어 ko/ja에는 실제 번역 dispatcher가 필요하다.
        var room = await fixture.SeedReadRoomAsync("DIRECT");
        var prefix = "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID") + ":missing:" + Guid.NewGuid().ToString("N");
        await using var host = await ChatRuntimeTestHost.StartAsync(fixture, prefix, configure: profileConfigured ? Profiles : null);

        // 실행
        using var request = Request(host, room.UserId, HttpMethod.Post, $"/api/v1/chat/rooms/{room.RoomId}/messages", "{\"content\":\"합성\"}");
        using var response = await host.Client.SendAsync(request);

        // 검증
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var readiness = await host.Client.GetAsync("/api/ready");
        using var readinessBody = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.Equal("READY", readinessBody.RootElement.GetProperty("read").GetString());
        Assert.Equal("NOT_CONFIGURED", readinessBody.RootElement.GetProperty("messages").GetString());
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(2, await read.ChatMessages.CountAsync(message => message.ChatRoomId == room.RoomId));
        Assert.Equal(0, await read.ChatMessageTranslations.Join(read.ChatMessages, translation => translation.ChatMessageId,
            message => message.Id, (translation, message) => message.ChatRoomId).CountAsync(roomId => roomId == room.RoomId));
    }

    [Theory]
    [InlineData(true, "OPEN_CHAT_ROOM_CLOSED")]
    [InlineData(false, "OPEN_CHAT_MEMBER_ACCESS_DENIED")]
    public async Task SendWaitsForOpenLifecycleCommit_ThenRejectsClosedOrDepartedMember(bool close, string expectedCode)
    {
        // 준비 — 외부 connection의 OPEN row 잠금으로 실제 HTTP 요청을 DB에서 대기시킨다.
        var room = await fixture.SeedReadRoomAsync("OPEN");
        await using (var db = await fixture.Contexts.CreateDbContextAsync())
        {
            db.OpenChatRooms.Add(new OpenChatRoomEntity
            {
                ChatRoomId = room.RoomId,
                Visibility = "PUBLIC",
                Status = "ACTIVE",
                MaxMemberCount = 10,
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            await db.SaveChangesAsync();
        }
        var prefix = "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID") + ":send-lock:" + Guid.NewGuid().ToString("N");
        await using var host = await ChatRuntimeTestHost.StartAsync(fixture, prefix, configure: Profiles);
        await using var blocker = await fixture.Contexts.CreateDbContextAsync();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.OpenChatRooms.FromSqlInterpolated($"SELECT * FROM open_chat_room WHERE chat_room_id = {room.RoomId} FOR UPDATE").ToListAsync();
        if (close)
        {
            await blocker.OpenChatRooms.Where(row => row.ChatRoomId == room.RoomId).ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, "CLOSED"));
        }
        else
        {
            await blocker.ChatRoomMembers.Where(row => row.Id == room.MemberId).ExecuteUpdateAsync(update => update.SetProperty(row => row.Active, false));
        }

        // 실행
        using var request = Request(host, room.UserId, HttpMethod.Post, $"/api/v1/chat/rooms/{room.RoomId}/messages", "{\"content\":\"거절될 합성 메시지\"}");
        var pending = host.Client.SendAsync(request);
        await using var observer = await fixture.Contexts.CreateDbContextAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await observer.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM performance_schema.data_lock_waits").SingleAsync(deadline.Token) == 0)
        {
            await Task.Delay(20, deadline.Token);
        }

        Assert.False(pending.IsCompleted);
        await transaction.CommitAsync();
        using var response = await pending;

        // 검증 — lifecycle commit 뒤 fresh snapshot에서 거절하며 부분 메시지/번역을 남기지 않는다.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, json.RootElement.GetProperty("body").GetProperty("errorCode").GetString());
        Assert.Equal(2, await observer.ChatMessages.CountAsync(row => row.ChatRoomId == room.RoomId));
    }

    private static void Profiles(IServiceCollection services)
    {
        services.AddSingleton<IChatMessageProfileReader, SyntheticProfiles>();
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

    private sealed class SyntheticProfiles : IChatMessageProfileReader
    {
        public Task<ChatUserMessageProfile> GetUserAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ChatUserMessageProfile(userId, "합성 사용자", $"synthetic-{userId}@example.invalid", null));
        }

        public Task<string?> ResolveObjectUrlAsync(string objectKey, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<string> GetAuditIdentityAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult($"USER:{userId}");
        }
    }
}
