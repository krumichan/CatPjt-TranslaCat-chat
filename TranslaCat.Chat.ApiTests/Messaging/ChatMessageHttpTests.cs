using System.Net;
using System.Text.Json;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.ApiTests.Messaging;

public sealed class ChatMessageHttpTests
{
    [Theory]
    [InlineData("", false, 101)]
    [InlineData("/after?cursorId=100&size=30", true, 31)]
    [InlineData("/anchor?anchorMessageId=100&beforeSize=0&afterSize=0", true, 1)]
    public async Task Actual_GET_routes_bind_queries_invoke_Application_and_serialize_page_contract(string suffix, bool forward, int limit)
    {
        // 준비
        await using var host = await ChatMessageHttpFixture.StartAsync();
        host.Transaction.Rows = [];

        // 실행
        using var response = await host.SendAsync("/api/v1/chat/rooms/41/messages" + suffix);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(200, json.GetProperty("resultCode").GetInt32());
        Assert.Equal(73, host.Transaction.UserId);
        Assert.Equal(41, host.Transaction.RoomId);
        Assert.Equal(forward, host.Transaction.Forward);
        Assert.Equal(limit, host.Transaction.Limit);
        Assert.False(json.GetProperty("body").GetProperty("hasNext").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("body").GetProperty("nextCursorId").ValueKind);
        if (suffix.StartsWith("/anchor", StringComparison.Ordinal))
        {
            Assert.Equal(100, json.GetProperty("body").GetProperty("anchorMessageId").GetInt64());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("body").GetProperty("previousCursorId").ValueKind);
            Assert.False(json.GetProperty("body").GetProperty("hasPrevious").GetBoolean());
        }
    }

    [Theory]
    [InlineData("9007199254740993", 9007199254740993L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("0x65", 101L)]
    [InlineData("%2365", 101L)]
    [InlineData("1%200%201", 101L)]
    [InlineData("%2B101", 101L)]
    [InlineData("%D9%A1%D9%A0%D9%A1", 101L)]
    public async Task Spring_numeric_query_conversion_preserves_long_precision_whitespace_hex_and_digits(string input, long expected)
    {
        // 준비
        await using var host = await ChatMessageHttpFixture.StartAsync();

        // 실행
        using var response = await host.SendAsync("/api/v1/chat/rooms/41/messages?cursorId=" + input);

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, host.Transaction.CursorId);
    }

    [Theory]
    [InlineData("/api/v1/chat/rooms/41/messages/after")]
    [InlineData("/api/v1/chat/rooms/41/messages/after?cursorId=")]
    [InlineData("/api/v1/chat/rooms/41/messages/anchor")]
    [InlineData("/api/v1/chat/rooms/41/messages/anchor?anchorMessageId=null")]
    [InlineData("/api/v1/chat/rooms/41/messages?cursorId=1.5")]
    [InlineData("/api/v1/chat/rooms/41/messages?cursorId=9223372036854775808")]
    [InlineData("/api/v1/chat/rooms/41/messages?cursorId=0x8000000000000000")]
    [InlineData("/api/v1/chat/rooms/41/messages/after?cursorId=101&size=2147483648")]
    [InlineData("/api/v1/chat/rooms/not-a-number/messages")]
    public async Task Binding_errors_use_BE_catch_all_500_envelope_without_Application(string path)
    {
        // 준비
        await using var host = await ChatMessageHttpFixture.StartAsync();

        // 실행
        using var response = await host.SendAsync(path);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(500, json.GetProperty("resultCode").GetInt32());
        Assert.Equal("", json.GetProperty("body").GetProperty("errorCode").GetString());
        Assert.Equal("", json.GetProperty("body").GetProperty("trace").GetString());
        Assert.Equal(0, host.Transaction.ExecuteCount);
    }

    [Theory]
    [InlineData("?cursorId=0", "", "cursorId는 1 이상이어야 합니다.")]
    [InlineData("/after?cursorId=0", "CHAT_MESSAGE_CURSOR_INVALID", "cursorId는 1 이상이어야 합니다.")]
    [InlineData("/after?cursorId=100&size=101", "CHAT_MESSAGE_FORWARD_SIZE_INVALID", "size는 1 이상 100 이하여야 합니다.")]
    [InlineData("/anchor?anchorMessageId=100&beforeSize=-1", "CHAT_MESSAGE_ANCHOR_SIZE_INVALID", "beforeSize는 0 이상 100 이하여야 합니다.")]
    public async Task Validly_bound_invalid_values_preserve_Application_business_status_code_and_message(string suffix, string code, string message)
    {
        // 준비
        await using var host = await ChatMessageHttpFixture.StartAsync();

        // 실행
        using var response = await host.SendAsync("/api/v1/chat/rooms/41/messages" + suffix);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, json.GetProperty("body").GetProperty("errorCode").GetString());
        Assert.Equal($"Message <{message}>", json.GetProperty("message").GetString());
        Assert.Equal(0, host.Transaction.ExecuteCount);
    }

    [Theory]
    [InlineData("\"  synthetic  \"", "synthetic")]
    [InlineData("3", "3")]
    [InlineData("-2.5e+3", "-2.5e+3")]
    [InlineData("true", "true")]
    [InlineData("\"\\u2003\"", "\u2003")]
    public async Task POST_preserves_String_coercion_real_Application_trim_and_HTTP200_with_created_envelope(string contentJson, string expected)
    {
        // 준비
        await using var host = await ChatMessageHttpFixture.StartAsync();

        // 실행
        using var response = await host.SendAsync(body: $"{{\"content\":{contentJson}}}");
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증: 저장소/commit은 fake지만 Controller와 Application 및 JSON serializer는 실제 구현이다.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(201, json.GetProperty("resultCode").GetInt32());
        Assert.Equal("Created", json.GetProperty("message").GetString());
        Assert.Equal(expected, host.Transaction.InsertedContent);
        Assert.Equal(expected, json.GetProperty("body").GetProperty("content").GetString());
        Assert.IsType<ChatMessageCreatedIntent>(Assert.Single(host.Transaction.Registered));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"content\":null}")]
    [InlineData("{\"content\":\" \"}")]
    [InlineData("{\"content\":{}}")]
    [InlineData("{\"content\":[]}")]
    [InlineData("{broken")]
    [InlineData("null")]
    public async Task Invalid_POST_body_is_rejected_before_Application(string body)
    {
        // 준비
        await using var host = await ChatMessageHttpFixture.StartAsync();

        // 실행
        using var response = await host.SendAsync(body: body);

        // 검증
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0, host.Transaction.ExecuteCount);
        Assert.Null(host.Transaction.InsertedContent);
    }

    [Theory]
    [InlineData(5000, HttpStatusCode.OK)]
    [InlineData(5001, HttpStatusCode.InternalServerError)]
    public async Task Raw_POST_size_is_checked_before_service_trims_the_message(int length, HttpStatusCode status)
    {
        // 준비: trim 후에는 1자여도 원문의 5001자는 DTO @Size 경계에서 거부해야 한다.
        await using var host = await ChatMessageHttpFixture.StartAsync();
        var content = "x" + new string(' ', length - 1);

        // 실행
        using var response = await host.SendAsync(body: JsonSerializer.Serialize(new
        {
            content
        }));

        // 검증
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(length == 5000 ? 1 : 0, host.Transaction.ExecuteCount);
        Assert.Equal(length == 5000 ? "x" : null, host.Transaction.InsertedContent);
    }

    [Fact]
    public async Task Empty_optional_cursor_defaults_to_latest_page_and_unknown_fields_are_ignored()
    {
        // 준비
        await using var host = await ChatMessageHttpFixture.StartAsync();

        // 실행
        using var response = await host.SendAsync("/api/v1/chat/rooms/41/messages?cursorId=&unused=true");

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(host.Transaction.CursorId);
        Assert.False(host.Transaction.Forward);
        Assert.Equal(101, host.Transaction.Limit);
    }

    [Fact]
    public async Task Missing_required_anchor_row_preserves_accessible_cursor_business_error()
    {
        // 준비
        await using var host = await ChatMessageHttpFixture.StartAsync();
        host.Transaction.AnchorAccessible = false;

        // 실행
        using var response = await host.SendAsync("/api/v1/chat/rooms/41/messages/anchor?anchorMessageId=101");
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("CHAT_MESSAGE_ANCHOR_NOT_ACCESSIBLE", json.GetProperty("body").GetProperty("errorCode").GetString());
        Assert.Equal("Message <Anchor 메시지를 찾을 수 없거나 접근할 수 없습니다.>", json.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("business", HttpStatusCode.BadRequest, "CHAT_ROOM_MEMBER_ACCESS_DENIED")]
    [InlineData("dependency", HttpStatusCode.ServiceUnavailable, "CHAT_MESSAGE_UNAVAILABLE")]
    [InlineData("internal", HttpStatusCode.InternalServerError, "")]
    public async Task Dependency_and_business_failures_keep_distinct_envelopes_without_leaking_internal_errors(string failure, HttpStatusCode status, string code)
    {
        // 준비
        await using var host = await ChatMessageHttpFixture.StartAsync();
        host.Transaction.Failure = failure switch
        {
            "business" => new ChatMessageException("합성 권한 거절", code),
            "dependency" => new ChatMessageDependencyUnavailableException("synthetic-private-dependency"),
            _ => new InvalidOperationException("synthetic-private-internal-error")
        };

        // 실행
        using var response = await host.SendAsync();
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, json.GetProperty("body").GetProperty("errorCode").GetString());
        Assert.DoesNotContain("synthetic-private", json.GetRawText());
    }

    [Fact]
    public async Task Missing_transaction_adapter_returns_explicit_503()
    {
        // 준비
        await using var host = await ChatMessageHttpFixture.StartAsync(registerTransaction: false);

        // 실행
        using var response = await host.SendAsync();
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("CHAT_MESSAGE_UNAVAILABLE", json.GetProperty("body").GetProperty("errorCode").GetString());
        Assert.Equal(0, host.Transaction.ExecuteCount);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("missing-id", HttpStatusCode.Forbidden)]
    public async Task Authentication_boundary_blocks_requests_before_Application(string? identity, HttpStatusCode status)
    {
        // 준비
        await using var host = await ChatMessageHttpFixture.StartAsync();

        // 실행
        using var response = await host.SendAsync(identity: identity);

        // 검증
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(0, host.Transaction.ExecuteCount);
    }
}
