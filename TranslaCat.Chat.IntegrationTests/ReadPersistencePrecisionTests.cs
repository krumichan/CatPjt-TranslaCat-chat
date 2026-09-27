using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class ReadPersistencePrecisionTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task RealMySql_advance_same_and_older_cursor_return_exactly_the_same_timestamp()
    {
        // 준비 — 100ns 잔여 tick이 있는 clock과 실제 HTTP/EF/MySQL/Redis/STOMP를 연결한다.
        var room = await fixture.SeedReadRoomAsync();
        var clock = new PrecisionTimeProvider();
        var prefix = "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID")
            + ":read-precision:" + Guid.NewGuid().ToString("N");
        await using var host = await ChatRuntimeTestHost.StartAsync(fixture, prefix,
            configure: services => services.AddSingleton<TimeProvider>(clock));
        using var socket = await host.ConnectAsync(room.UserId);
        await socket.SubscribeAsync("self", "/user/queue/chat/read");
        await socket.SubscribeAsync("room", $"/topic/chat/rooms/{room.RoomId}");

        // 실행 — 최초 전진 응답과 commit 후 자기/방 이벤트를 수신한다.
        using var firstResponse = await host.MarkReadAsync(room.UserId, room.RoomId, room.SecondId);
        using var firstJson = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var first = firstJson.RootElement.GetProperty("body");
        var firstSelf = await socket.ReceiveEventAsync("chat.read.updated");
        var firstMember = await socket.ReceiveEventAsync("chat.member.read.updated");

        // 검증 — 최초 응답부터 DB에 표현 가능한 값이며 독립 connection의 저장값과 같다.
        const string expectedTimestamp = "2026-09-26T12:34:56.724438Z";
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(expectedTimestamp, first.GetProperty("lastReadAt").GetString());
        Assert.Equal(expectedTimestamp, firstSelf.GetProperty("lastReadAt").GetString());
        Assert.Equal(expectedTimestamp, firstMember.GetProperty("readAt").GetString());
        await using (var persisted = await fixture.Contexts.CreateDbContextAsync())
        {
            var member = await persisted.ChatRoomMembers.SingleAsync(value => value.Id == room.MemberId);
            Assert.Equal(new DateTime(2026, 9, 26, 12, 34, 56).AddTicks(7_244_380), member.LastReadAt);
        }

        foreach (var candidate in new[] { room.SecondId, room.FirstId })
        {
            // 실행 — 요청마다 다른 시각을 공급해도 같은/과거 cursor의 저장값은 보존되어야 한다.
            clock.Now = clock.Now.AddMinutes(1);
            using var response = await host.MarkReadAsync(room.UserId, room.RoomId, candidate);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var body = json.RootElement.GetProperty("body");
            var self = await socket.ReceiveEventAsync("chat.read.updated");

            // 검증 — 실제 DB 재조회 뒤 no-op HTTP와 자기 이벤트가 최초 문자열과 byte-for-byte 동일하다.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(room.SecondId, body.GetProperty("lastReadMessageId").GetInt64());
            Assert.Equal(first.GetProperty("lastReadAt").GetRawText(), body.GetProperty("lastReadAt").GetRawText());
            Assert.Equal(first.GetProperty("lastReadAt").GetRawText(), self.GetProperty("lastReadAt").GetRawText());
            Assert.Equal(0, body.GetProperty("unreadCount").GetInt64());
        }

        await using var finalRead = await fixture.Contexts.CreateDbContextAsync();
        var finalMember = await finalRead.ChatRoomMembers.SingleAsync(value => value.Id == room.MemberId);
        Assert.Equal(room.SecondId, finalMember.LastReadMessageId);
        Assert.Equal(new DateTime(2026, 9, 26, 12, 34, 56).AddTicks(7_244_380), finalMember.LastReadAt);
    }

    private sealed class PrecisionTimeProvider : TimeProvider
    {
        public DateTimeOffset Now
        {
            get; set;
        } =
            new DateTimeOffset(2026, 9, 26, 12, 34, 56, TimeSpan.Zero).AddTicks(7_244_381);

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }
}
