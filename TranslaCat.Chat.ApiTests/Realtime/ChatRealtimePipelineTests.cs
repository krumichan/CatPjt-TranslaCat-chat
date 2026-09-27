using System.Text;
using System.Text.Json;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.ApiTests.Realtime;

public sealed class ChatRealtimePipelineTests
{
    [Fact]
    public async Task Native_CONNECT_receives_negotiated_protocol_and_zero_server_heartbeat()
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync();

        // 실행
        using var client = await host.ConnectAsync();

        // 검증
        Assert.Equal("v12.stomp", client.Socket.SubProtocol);
        var presence = Assert.Single(host.Ports.Connected);
        Assert.Equal(73, presence.UserId);
        Assert.NotEmpty(presence.SessionId);
    }

    [Theory]
    [InlineData("invalid-signature")]
    [InlineData("missing-authorization")]
    [InlineData("send-before-connect")]
    [InlineData("unsupported-version")]
    public async Task Authentication_and_protocol_failures_return_ERROR_without_creating_session(string failure)
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync();
        using var client = await host.OpenAsync();
        var token = host.CreateToken(invalidSignature: failure == "invalid-signature");
        var frame = failure switch
        {
            "missing-authorization" => "CONNECT\naccept-version:1.2\n\n\0",
            "send-before-connect" => "SEND\ndestination:/app/chat/rooms/41/messages\n\n{}\0",
            "unsupported-version" => $"CONNECT\naccept-version:1.0\nAuthorization:Bearer {token}\n\n\0",
            _ => $"CONNECT\naccept-version:1.2\nAuthorization:Bearer {token}\n\n\0"
        };

        // 실행
        await client.SendAsync(frame);
        var response = await client.ReceiveAsync();

        // 검증
        Assert.StartsWith("ERROR\n", response);
        Assert.DoesNotContain(token, response);
        Assert.Empty(host.Ports.Connected);
        Assert.Empty(host.Ports.Sent);
    }

    [Fact]
    public async Task Fragmented_SEND_with_utf8_length_calls_message_port_once_and_returns_escaped_receipt()
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync();
        using var client = await host.ConnectAsync();
        const string body = "{\"content\":\"합성 메시지 🌱\"}";
        var frame = $"\nSEND\ndestination:/app/chat/rooms/41/messages\ncontent-type:application/json\ncontent-length:{Encoding.UTF8.GetByteCount(body)}\nreceipt:send\\c1\n\n{body}\0";

        // 실행
        await client.SendAsync(frame, Encoding.UTF8.GetByteCount(frame[..(frame.IndexOf("합성", StringComparison.Ordinal) + 1)]) - 1);
        var response = await client.ReceiveAsync();

        // 검증: 단순 transport 수신 확인이며 실제 DB commit 검증은 아니다.
        Assert.StartsWith("RECEIPT\n", response);
        Assert.Contains("receipt-id:send\\c1\n", response);
        Assert.Equal((73L, 41L, "합성 메시지 🌱"), Assert.Single(host.Ports.Sent));
    }

    [Fact]
    public async Task Room_delivery_rechecks_membership_and_unsubscribe_removes_delivery()
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync();
        using var member = await host.ConnectAsync();
        using var removedMember = await host.ConnectAsync(74);
        await member.SubscribeAsync("room", "/topic/chat/rooms/41");
        await removedMember.SubscribeAsync("room", "/topic/chat/rooms/41");
        host.Ports.DeniedUsers[74] = true;

        // 실행 / 검증: 탈퇴 직전 구독을 갖고 있더라도 새 메시지는 차단된다.
        await host.Broker.PublishRoomAsync(41, "{\"eventType\":\"chat.message.created\",\"messageId\":101}");
        var delivered = await member.ReceiveAsync();
        Assert.StartsWith("MESSAGE\n", delivered);
        Assert.Contains("destination:/topic/chat/rooms/41\n", delivered);
        Assert.Contains("subscription:room\n", delivered);
        Assert.Contains("\"messageId\":101", delivered);
        await removedMember.AssertBarrierAsync("no-stale-delivery");

        // 실행 / 검증: 명시적으로 구독을 제거한 뒤에도 전달하지 않는다.
        await member.SendAsync("UNSUBSCRIBE\nid:room\nreceipt:unsub\n\n\0");
        Assert.Contains("receipt-id:unsub\n", await member.ReceiveAsync());
        await host.Broker.PublishRoomAsync(41, "{\"messageId\":102}");
        await member.AssertBarrierAsync("no-unsubscribed-delivery");
    }

    [Fact]
    public async Task User_queues_isolate_accounts_and_keep_private_removal_notice_after_membership_ends()
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync();
        using var owner = await host.ConnectAsync();
        using var other = await host.ConnectAsync(74);
        await owner.SubscribeAsync("self", "/user/queue/chat/read");
        await other.SubscribeAsync("self", "/user/queue/chat/read");
        await owner.SubscribeAsync("open", "/user/queue/chat/open-rooms/41");

        // 실행 / 검증: 이름 기반 user destination을 다른 계정에 전달하지 않는다.
        await host.Broker.PublishUserAsync("synthetic-73@example.invalid", "/queue/chat/read", "{\"unreadCount\":3}");
        Assert.Contains("\"unreadCount\":3", await owner.ReceiveAsync());
        await other.AssertBarrierAsync("not-other-account");

        // 실행 / 검증: private 강퇴/퇴장 알림은 방 접근권 상실 뒤에도 대상 계정에 전달한다.
        host.Ports.DeniedUsers[73] = true;
        await host.Broker.PublishUserAsync("synthetic-73@example.invalid", "/queue/chat/open-rooms/41", "{\"eventType\":\"chat.open.member.banned\"}");
        Assert.Contains("chat.open.member.banned", await owner.ReceiveAsync());
    }

    [Theory]
    [InlineData("/topic/chat/rooms/42")]
    [InlineData("/user/queue/chat/open-rooms/42")]
    [InlineData("/topic/unrelated")]
    [InlineData("/queue/chat/read")]
    [InlineData("/user/someone/queue/chat/read")]
    public async Task Unauthorized_or_unknown_subscription_destination_is_rejected(string destination)
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync();
        using var client = await host.ConnectAsync();

        // 실행
        await client.SendAsync($"SUBSCRIBE\nid:denied\ndestination:{destination}\n\n\0");

        // 검증
        Assert.StartsWith("ERROR\n", await client.ReceiveAsync());
        Assert.Empty(host.Ports.Sent);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"content\":null}")]
    [InlineData("{\"content\":\"   \"}")]
    [InlineData("{\"content\":[]}")]
    [InlineData("{\"content\":{}}")]
    [InlineData("invalid-json")]
    public async Task Invalid_message_body_never_calls_message_port(string body)
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync();
        using var client = await host.ConnectAsync();
        await client.SubscribeAsync("errors", "/user/queue/errors");

        // 실행
        await client.SendAsync($"SEND\ndestination:/app/chat/rooms/41/messages\n\n{body}\0");

        // 검증
        var response = await client.ReceiveAsync();
        Assert.StartsWith("MESSAGE\n", response);
        Assert.Contains("CHAT_WEBSOCKET_INTERNAL_ERROR", response);
        Assert.Empty(host.Ports.Sent);
        await client.AssertBarrierAsync("connection-remains-open");
    }

    [Theory]
    [InlineData("3", "3")]
    [InlineData("-2.5e+3", "-2.5e+3")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("\"\\u00a0\"", "\u00a0")]
    [InlineData("\"\\u2003\"", "\u2003")]
    public async Task Legacy_String_scalar_coercion_and_Java_trim_validation_are_preserved(string jsonValue, string expected)
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync();
        using var client = await host.ConnectAsync();

        // 실행
        await client.SendAsync($"SEND\ndestination:/app/chat/rooms/41/messages\nreceipt:coercion\n\n{{\"content\":{jsonValue}}}\0");
        var response = await client.ReceiveAsync();

        // 검증: Application의 normalization은 port 구현 책임이다. 여기서는 원본 DTO 입력을 보존한다.
        Assert.Contains("receipt-id:coercion\n", response);
        Assert.Equal(expected, Assert.Single(host.Ports.Sent).Content);
    }

    [Theory]
    [InlineData(5000, true)]
    [InlineData(5001, false)]
    public async Task Raw_message_length_limit_is_enforced_before_Application(int length, bool allowed)
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync();
        using var client = await host.ConnectAsync();
        await client.SubscribeAsync("errors", "/user/queue/errors");
        var content = new string('x', length);

        // 실행
        await client.SendAsync($"SEND\ndestination:/app/chat/rooms/41/messages\nreceipt:length\n\n{{\"content\":\"{content}\"}}\0");
        var response = await client.ReceiveAsync();

        // 검증
        Assert.StartsWith(allowed ? "RECEIPT\n" : "MESSAGE\n", response);
        Assert.Equal(allowed ? 1 : 0, host.Ports.Sent.Count);
    }

    [Fact]
    public async Task Application_business_failure_uses_private_error_envelope_and_preserves_connection()
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync();
        host.Ports.SendFailure = new ChatMessageException("합성 업무 실패", "SYNTHETIC_REASON");
        using var client = await host.ConnectAsync();
        await client.SubscribeAsync("errors", "/user/queue/errors");

        // 실행
        await client.SendAsync("SEND\ndestination:/app/chat/rooms/41/messages\n\n{\"content\":\"synthetic\"}\0");
        var response = await client.ReceiveAsync();
        using var json = JsonDocument.Parse(response[(response.IndexOf("\n\n", StringComparison.Ordinal) + 2)..^1]);

        // 검증: 원본 WS business envelope은 개별 내부 errorCode 대신 공통 code를 사용한다.
        Assert.StartsWith("MESSAGE\n", response);
        Assert.Equal("chat.error", json.RootElement.GetProperty("eventType").GetString());
        Assert.Equal("CHAT_WEBSOCKET_BUSINESS_ERROR", json.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("합성 업무 실패", json.RootElement.GetProperty("message").GetString());
        Assert.Equal(FixedReadTimeProvider.Now, json.RootElement.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Empty(host.Ports.Sent);
        await client.AssertBarrierAsync("business-error-open");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Missing_production_access_or_message_adapter_fails_closed(bool registerAccess, bool registerSender)
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync(registerAccess, registerSender);
        using var client = await host.ConnectAsync();
        await client.SubscribeAsync("errors", "/user/queue/errors");

        // 실행
        await client.SendAsync("SEND\ndestination:/app/chat/rooms/41/messages\n\n{\"content\":\"synthetic\"}\0");

        // 검증
        Assert.StartsWith(registerAccess ? "MESSAGE\n" : "ERROR\n", await client.ReceiveAsync());
        Assert.Empty(host.Ports.Sent);
    }

    [Fact]
    public async Task Partial_session_registration_failure_still_cleans_up_same_session()
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync();
        host.Ports.FailAfterSessionRegistration = true;
        using var client = await host.OpenAsync();

        // 실행
        await client.SendAsync($"CONNECT\naccept-version:1.2\nAuthorization:Bearer {host.CreateToken()}\n\n\0");
        var response = await client.ReceiveAsync();
        await host.Ports.DisconnectObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 검증
        Assert.StartsWith("ERROR\n", response);
        Assert.Equal(Assert.Single(host.Ports.Connected), Assert.Single(host.Ports.Disconnected));
    }

    [Fact]
    public async Task DISCONNECT_receipt_precedes_socket_close_and_lifecycle_cleanup()
    {
        // 준비
        await using var host = await ChatRealtimeTestHost.StartAsync();
        using var client = await host.ConnectAsync();

        // 실행
        await client.SendAsync("DISCONNECT\nreceipt:bye\n\n\0");
        var receipt = await client.ReceiveAsync();
        await host.Ports.DisconnectObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 검증
        Assert.Contains("receipt-id:bye\n", receipt);
        Assert.Equal(Assert.Single(host.Ports.Connected), Assert.Single(host.Ports.Disconnected));
    }
}
