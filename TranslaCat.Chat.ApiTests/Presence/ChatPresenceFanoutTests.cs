using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TranslaCat.Chat.Api.Presence;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.ApiTests.Realtime;
using TranslaCat.Chat.Application.Presence;

namespace TranslaCat.Chat.ApiTests.Presence;

public sealed class ChatPresenceFanoutTests
{
    [Theory]
    [InlineData("DIRECT", "public-profile-73")]
    [InlineData("GROUP", "9223372036854775807")]
    [InlineData("OPEN", "9223372036854775807")]
    public async Task PresenceWireEvent_UsesRoomSpecificIdentityAndSourceTimestamp(string roomType, string expectedRef)
    {
        // 준비: JWT/HTTP upgrade/STOMP/broker는 실제이며 membership/profile은 합성 port다.
        await using var host = await ChatRealtimeTestHost.StartAsync();
        using var client = await host.ConnectAsync();
        await client.SubscribeAsync("presence", "/topic/chat/rooms/41");
        var reader = new SyntheticRooms(new ChatPresenceRoomMember(41, roomType, long.MaxValue, false));
        var fanout = new ChatPresenceFanout(reader, host.Broker, new(TimeZoneInfo.Utc),
            NullLogger<ChatPresenceFanout>.Instance, new SyntheticProfiles());

        // 실행
        await fanout.HandleAsync(new(73, true, new DateTime(2026, 9, 27, 1, 2, 3, DateTimeKind.Unspecified)));
        var frame = await client.ReceiveAsync();
        using var payload = JsonDocument.Parse(frame.Split("\n\n", 2)[1].TrimEnd('\0'));

        // 검증
        Assert.StartsWith("MESSAGE\n", frame);
        var body = payload.RootElement;
        Assert.Equal(6, body.EnumerateObject().Count());
        Assert.Equal("chat.presence.changed", body.GetProperty("eventType").GetString());
        Assert.Equal(41, body.GetProperty("roomId").GetInt64());
        Assert.Equal(roomType, body.GetProperty("roomType").GetString());
        Assert.Equal(expectedRef, body.GetProperty("memberRef").GetString());
        Assert.True(body.GetProperty("online").GetBoolean());
        Assert.Equal("2026-09-27T01:02:03Z", body.GetProperty("occurredAt").GetString());
        Assert.Equal(73, reader.UserId);
    }

    [Theory]
    [InlineData("DIRECT")]
    [InlineData("GROUP")]
    [InlineData("OPEN")]
    public async Task PrivateAiRoom_HidesAllHumanPresence(string roomType)
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync();
        using var client = await host.ConnectAsync();
        await client.SubscribeAsync("private", "/topic/chat/rooms/41");
        var fanout = new ChatPresenceFanout(new SyntheticRooms(new ChatPresenceRoomMember(41, roomType, 123, true)), host.Broker,
            new ChatReadTimestampFormatter(TimeZoneInfo.Utc), NullLogger<ChatPresenceFanout>.Instance, new SyntheticProfiles());

        // 실행
        await fanout.HandleAsync(new(73, false, new DateTime(2026, 9, 27)));

        // 검증: receipt barrier 이전에 presence MESSAGE가 없음을 실제 socket에서 확인한다.
        await client.AssertBarrierAsync("private-hidden");
    }

    [Fact]
    public async Task MissingPublicIdAdapter_DoesNotExposeInternalUserIdInDirectRoom()
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync();
        using var client = await host.ConnectAsync();
        await client.SubscribeAsync("direct", "/topic/chat/rooms/41");
        var fanout = new ChatPresenceFanout(new SyntheticRooms(new ChatPresenceRoomMember(41, "DIRECT", 123, false)), host.Broker,
            new ChatReadTimestampFormatter(TimeZoneInfo.Utc), NullLogger<ChatPresenceFanout>.Instance);

        // 실행
        await fanout.HandleAsync(new(73, true, new DateTime(2026, 9, 27)));

        // 검증
        await client.AssertBarrierAsync("identity-unavailable");
    }

    [Fact]
    public async Task ProfileFailure_DoesNotBlockIndependentGroupFanout()
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync();
        using var client = await host.ConnectAsync();
        await client.SubscribeAsync("group", "/topic/chat/rooms/41");
        var rooms = new SyntheticRooms(new(41, "DIRECT", 123, false), new(41, "GROUP", 456, false));
        var fanout = new ChatPresenceFanout(rooms, host.Broker, new(TimeZoneInfo.Utc),
            NullLogger<ChatPresenceFanout>.Instance, new SyntheticProfiles { Fail = true });

        // 실행
        await fanout.HandleAsync(new(73, true, new DateTime(2026, 9, 27)));
        var frame = await client.ReceiveAsync();

        // 검증
        Assert.Contains("\"roomType\":\"GROUP\"", frame);
        Assert.Contains("\"memberRef\":\"456\"", frame);
        await client.AssertBarrierAsync("one-group-only");
    }

    private sealed class SyntheticRooms(params ChatPresenceRoomMember[] memberships) : IChatPresenceRoomReader
    {
        public long? UserId
        {
            get; private set;
        }

        public Task<IReadOnlyList<ChatPresenceRoomMember>> FindActiveMembershipsAsync(long userId, CancellationToken cancellationToken)
        {
            UserId = userId;
            return Task.FromResult<IReadOnlyList<ChatPresenceRoomMember>>(memberships);
        }
    }

    private sealed class SyntheticProfiles : IChatPresenceProfileReader
    {
        public bool Fail
        {
            get; init;
        }

        public Task<string?> FindPublicIdAsync(long userId, CancellationToken cancellationToken)
        {
            return Fail
            ? Task.FromException<string?>(new InvalidOperationException("synthetic account service unavailable"))
            : Task.FromResult<string?>("public-profile-" + userId);
        }
    }
}
