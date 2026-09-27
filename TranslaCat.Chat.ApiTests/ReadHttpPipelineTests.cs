using System.Net;
using System.Text.Json;
using TranslaCat.Chat.Application.Read;
using TranslaCat.Chat.Domain;

namespace TranslaCat.Chat.ApiTests;

public sealed class ReadHttpPipelineTests
{
    [Fact]
    public async Task Patch_advances_using_real_application_and_returns_exact_contract()
    {
        // 준비
        await using var fixture = await ReadHttpFixture.StartAsync();

        // 실행
        using var response = await fixture.PatchAsync("{\"lastReadMessageId\":101}");
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증: 실제 HTTP 직렬화와 저장/이벤트 의도를 함께 확인한다.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        AssertFields(json, "resultCode", "message", "body", "guid", "createDate");
        Assert.Equal(200, json.GetProperty("resultCode").GetInt32());
        Assert.Equal("OK", json.GetProperty("message").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("guid").GetString()));
        Assert.Equal("2026-09-26 12:34:56", json.GetProperty("createDate").GetString());

        var body = json.GetProperty("body");
        AssertFields(body, "chatRoomId", "lastReadMessageId", "lastReadAt", "unreadCount");
        Assert.Equal(41, body.GetProperty("chatRoomId").GetInt64());
        Assert.Equal(101, body.GetProperty("lastReadMessageId").GetInt64());
        Assert.Equal("2026-09-26T12:34:56Z", body.GetProperty("lastReadAt").GetString());
        Assert.Equal(7, body.GetProperty("unreadCount").GetInt64());
        Assert.Equal(1, fixture.Transaction.SaveCount);
        Assert.Equal(73, fixture.Transaction.LoginUserId);
        Assert.Equal(41, fixture.Transaction.RequestedRoomId);
        Assert.Equal(101, fixture.Transaction.RequestedMessageId);
        Assert.True(fixture.Transaction.Committed);
        Assert.Equal(new ReadCursor(101, new DateTime(2026, 9, 26, 12, 34, 56)), fixture.Transaction.CommittedCursor);
        Assert.Equal(new[] { "access", "member", "message", "save", "unread", "self-intent", "member-intent", "commit" }, fixture.Transaction.Calls);
        Assert.Equal(2, fixture.Transaction.DeliveredPayloads.Count);
        Assert.Equal(7, fixture.Transaction.DeliveredPayloads[0].GetProperty("unreadCount").GetInt64());
        Assert.Equal(100, fixture.Transaction.DeliveredPayloads[1].GetProperty("previousLastReadMessageId").GetInt64());
        Assert.Equal(101, fixture.Transaction.DeliveredPayloads[1].GetProperty("lastReadMessageId").GetInt64());
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(99, true)]
    public async Task Noop_preserves_previous_cursor_and_nullable_time_and_self_event(long candidate, bool nullTime)
    {
        // 준비
        await using var fixture = await ReadHttpFixture.StartAsync();
        var previousTime = nullTime ? (DateTime?)null : new DateTime(2026, 9, 21, 1, 2, 3);
        fixture.Transaction.Member = fixture.Transaction.Member! with
        {
            Cursor = new ReadCursor(100, previousTime)
        };
        fixture.Transaction.Message = fixture.Transaction.Message! with
        {
            Id = candidate
        };
        fixture.Transaction.UnreadCount = 13;

        // 실행
        using var response = await fixture.PatchAsync($"{{\"lastReadMessageId\":{candidate}}}");
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(100, json.GetProperty("body").GetProperty("lastReadMessageId").GetInt64());
        Assert.Equal(13, json.GetProperty("body").GetProperty("unreadCount").GetInt64());
        Assert.Equal(0, fixture.Transaction.SaveCount);
        Assert.Null(fixture.Transaction.CommittedCursor);
        var selfEvent = Assert.Single(fixture.Transaction.DeliveredPayloads);
        Assert.Equal("chat.read.updated", selfEvent.GetProperty("eventType").GetString());
        Assert.Equal(13, selfEvent.GetProperty("unreadCount").GetInt64());
        Assert.Equal(nullTime ? JsonValueKind.Null : JsonValueKind.String, selfEvent.GetProperty("lastReadAt").ValueKind);
        Assert.Equal(nullTime ? null : "2026-09-21T01:02:03Z", selfEvent.GetProperty("lastReadAt").GetString());
        Assert.Equal(selfEvent.GetProperty("lastReadAt").GetRawText(), json.GetProperty("body").GetProperty("lastReadAt").GetRawText());
    }

    [Theory]
    [InlineData(ChatReadRoomType.Direct, false)]
    [InlineData(ChatReadRoomType.Group, false)]
    [InlineData(ChatReadRoomType.Open, true)]
    public async Task Public_room_event_keeps_reader_identity_distinction(ChatReadRoomType roomType, bool isOpen)
    {
        // 준비
        await using var fixture = await ReadHttpFixture.StartAsync();
        fixture.Transaction.Member = fixture.Transaction.Member! with
        {
            RoomType = roomType
        };

        // 실행
        using var response = await fixture.PatchAsync("{\"lastReadMessageId\":101}");

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var roomEvent = fixture.Transaction.DeliveredPayloads[1];
        Assert.Equal(isOpen ? JsonValueKind.Null : JsonValueKind.Number, roomEvent.GetProperty("readerUserId").ValueKind);
        Assert.Equal(isOpen ? JsonValueKind.Number : JsonValueKind.Null, roomEvent.GetProperty("readerOpenChatMemberId").ValueKind);
        Assert.Equal(isOpen ? 91 : 73, roomEvent.GetProperty(isOpen ? "readerOpenChatMemberId" : "readerUserId").GetInt64());
    }

    [Theory]
    [InlineData("9007199254740993", 9007199254740993L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("\"9007199254740993\"", 9007199254740993L)]
    [InlineData("\"9223372036854775807\"", long.MaxValue)]
    [InlineData("9223372036854775808.0", long.MaxValue)]
    [InlineData("9.223372036854776e18", long.MaxValue)]
    [InlineData("\" +00151 \"", 151L)]
    [InlineData("\"١٥١\"", 151L)]
    [InlineData("\"１５１\"", 151L)]
    [InlineData("151.9", 151L)]
    [InlineData("1e2", 100L)]
    public async Task Binding_preserves_long_integer_precision_and_legacy_numeric_coercion(string value, long expected)
    {
        // 준비
        await using var fixture = await ReadHttpFixture.StartAsync();
        fixture.Transaction.Member = fixture.Transaction.Member! with
        {
            Cursor = new ReadCursor(null, null)
        };
        fixture.Transaction.Message = fixture.Transaction.Message! with
        {
            Id = expected
        };

        // 실행
        using var response = await fixture.PatchAsync($"{{\"lastReadMessageId\":{value}}}");
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, json.GetProperty("body").GetProperty("lastReadMessageId").GetInt64());
        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), json.GetProperty("body").GetProperty("lastReadMessageId").GetRawText());
        Assert.Equal(expected, fixture.Transaction.CommittedCursor?.LastReadMessageId);
        Assert.Equal(expected, fixture.Transaction.RequestedMessageId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"lastReadMessageId\":null}")]
    [InlineData("{\"lastReadMessageId\":0}")]
    [InlineData("{\"lastReadMessageId\":-1}")]
    [InlineData("{\"lastReadMessageId\":9223372036854775808}")]
    [InlineData("{\"lastReadMessageId\":true}")]
    [InlineData("{\"lastReadMessageId\":\"abc\"}")]
    [InlineData("{\"lastReadMessageId\":\"151.0\"}")]
    [InlineData("{\"lastReadMessageId\":\"1e2\"}")]
    [InlineData("{\"lastReadMessageId\":\"NULL\"}")]
    [InlineData("{\"lastReadMessageId\":\"9223372036854775808\"}")]
    [InlineData("{\"lastReadMessageId\":[]}")]
    [InlineData("{\"lastReadMessageId\":{}}")]
    [InlineData("{\"lastReadMessageId\":0.9}")]
    [InlineData("{\"lastReadMessageId\":9.223372036854778e18}")]
    [InlineData("{\"lastReadMessageId\":\"¹⁵¹\"}")]
    [InlineData("{\"lastReadMessageId\":\"\u00a0151\u00a0\"}")]
    [InlineData("{\"lastReadMessageId\":\"\"}")]
    [InlineData("{\"lastReadMessageId\":\"null\"}")]
    [InlineData("{\"LastReadMessageId\":101}")]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("")]
    public async Task Invalid_body_uses_legacy_error_envelope_without_application_execution(string input)
    {
        // 준비
        await using var fixture = await ReadHttpFixture.StartAsync();

        // 실행
        using var response = await fixture.PatchAsync(input);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증: BE Advice의 catch-all status를 보존하며 400 정규화 여부는 별도 결정이다.
        AssertError(response, json, HttpStatusCode.InternalServerError, "", "/api/v1/chat/rooms/41/read");
        Assert.Equal(0, fixture.Transaction.ExecuteCount);
        Assert.Empty(fixture.Transaction.DeliveredPayloads);
    }

    [Theory]
    [InlineData("not-a-long")]
    [InlineData("9223372036854775808")]
    public async Task Invalid_route_binding_is_rejected_before_application(string roomId)
    {
        // 준비
        await using var fixture = await ReadHttpFixture.StartAsync();

        // 실행
        using var response = await fixture.PatchAsync("{\"lastReadMessageId\":101}", roomId);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        AssertError(response, json, HttpStatusCode.InternalServerError, "", $"/api/v1/chat/rooms/{roomId}/read");
        Assert.Equal(0, fixture.Transaction.ExecuteCount);
    }

    [Fact]
    public async Task Negative_room_id_is_bound_without_new_positive_validation()
    {
        // 준비
        await using var fixture = await ReadHttpFixture.StartAsync();
        fixture.Transaction.Member = fixture.Transaction.Member! with
        {
            ChatRoomId = -41
        };
        fixture.Transaction.Message = fixture.Transaction.Message! with
        {
            ChatRoomId = -41
        };

        // 실행
        using var response = await fixture.PatchAsync("{\"lastReadMessageId\":101}", "-41");
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(-41, json.GetProperty("body").GetProperty("chatRoomId").GetInt64());
        Assert.Equal(-41, fixture.Transaction.RequestedRoomId);
        Assert.Equal(1, fixture.Transaction.ExecuteCount);
    }

    [Fact]
    public async Task Unknown_fields_are_ignored_and_cannot_replace_authenticated_user()
    {
        // 준비
        await using var fixture = await ReadHttpFixture.StartAsync();

        // 실행
        using var response = await fixture.PatchAsync("{\"lastReadMessageId\":101,\"userId\":999,\"syntheticExtra\":true}");

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(101, fixture.Transaction.RequestedMessageId);
        Assert.Equal(73, fixture.Transaction.LoginUserId);
        Assert.Equal(73, fixture.Transaction.DeliveredPayloads[0].GetProperty("userId").GetInt64());
    }

    [Theory]
    [InlineData("access", "OPEN_CHAT_BANNED")]
    [InlineData("member", "CHAT_ROOM_MEMBER_ACCESS_DENIED")]
    [InlineData("missing", "CHAT_ROOM_READ_MESSAGE_NOT_FOUND")]
    [InlineData("wrong-room", "CHAT_ROOM_READ_MESSAGE_NOT_FOUND")]
    [InlineData("deleted", "CHAT_ROOM_READ_MESSAGE_NOT_FOUND")]
    [InlineData("unsent", "CHAT_ROOM_READ_MESSAGE_NOT_ACCESSIBLE")]
    [InlineData("before-join", "CHAT_ROOM_READ_MESSAGE_NOT_ACCESSIBLE")]
    public async Task Noop_candidate_still_passes_application_validation_and_maps_business_failure(string failure, string code)
    {
        // 준비: 기존 cursor와 같은 ID라도 조회·접근·메시지 검증을 생략하지 않는다.
        await using var fixture = await ReadHttpFixture.StartAsync();
        fixture.Transaction.Message = fixture.Transaction.Message! with
        {
            Id = 100
        };
        switch (failure)
        {
            case "access":
                fixture.Transaction.AccessFailure = new ChatReadException(code, "합성 접근 거부");
                break;
            case "member":
                fixture.Transaction.Member = null;
                break;
            case "missing":
                fixture.Transaction.Message = null;
                break;
            case "wrong-room":
                fixture.Transaction.Message = fixture.Transaction.Message with
                {
                    ChatRoomId = 99
                };
                break;
            case "deleted":
                fixture.Transaction.Message = fixture.Transaction.Message with
                {
                    DeletedAt = new DateTime(2026, 9, 23)
                };
                break;
            case "unsent":
                fixture.Transaction.Message = fixture.Transaction.Message with
                {
                    IsSent = false
                };
                break;
            case "before-join":
                fixture.Transaction.Message = fixture.Transaction.Message with
                {
                    CreatedAt = new DateTime(2026, 9, 19)
                };
                break;
        }

        // 실행
        using var response = await fixture.PatchAsync("{\"lastReadMessageId\":100}");
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        AssertError(response, json, HttpStatusCode.BadRequest, code, "/api/v1/chat/rooms/41/read");
        var expectedMessage = failure switch
        {
            "access" => "합성 접근 거부",
            "member" => "채팅방 멤버가 아니거나 접근 권한이 없습니다.",
            "missing" or "wrong-room" or "deleted" => "읽음 처리할 메시지를 찾을 수 없습니다.",
            "unsent" => "전송 완료된 메시지만 읽음 처리할 수 있습니다.",
            "before-join" => "현재 참여 시점 이전 메시지는 읽음 처리할 수 없습니다.",
            _ => throw new InvalidOperationException("Unknown test scenario.")
        };
        Assert.Equal($"Message <{expectedMessage}>", json.GetProperty("message").GetString());
        Assert.Equal(1, fixture.Transaction.ExecuteCount);
        Assert.Equal(0, fixture.Transaction.SaveCount);
        Assert.True(fixture.Transaction.RolledBack);
        Assert.Empty(fixture.Transaction.DeliveredPayloads);
    }

    [Fact]
    public async Task Http_success_and_mapped_events_wait_for_separate_commit_confirmation()
    {
        // 준비
        await using var fixture = await ReadHttpFixture.StartAsync();
        fixture.Transaction.HoldCommit = true;

        // 실행: callback 완료까지 진행시키되 commit은 아직 허용하지 않는다.
        var request = fixture.PatchAsync("{\"lastReadMessageId\":101}");
        await fixture.Transaction.WorkCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 검증: save 완료와 등록된 이벤트만 있고 성공 HTTP 응답/전달은 없다.
        Assert.False(request.IsCompleted);
        Assert.Equal(1, fixture.Transaction.SaveCount);
        Assert.Equal(2, fixture.Transaction.PendingCount);
        Assert.False(fixture.Transaction.Committed);
        Assert.Empty(fixture.Transaction.DeliveredPayloads);

        // 실행 / 검증: 명시적으로 commit을 허용한 뒤에 응답과 payload가 완성된다.
        fixture.Transaction.AllowCommit();
        using var response = await request;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(fixture.Transaction.Committed);
        Assert.Equal(2, fixture.Transaction.DeliveredPayloads.Count);
        Assert.Equal("2026-09-26T12:34:57Z", fixture.Transaction.DeliveredPayloads[0].GetProperty("occurredAt").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_commit_or_explicit_rollback_returns_error_without_delivery(bool explicitRollback)
    {
        // 준비
        await using var fixture = await ReadHttpFixture.StartAsync();
        fixture.Transaction.HoldCommit = explicitRollback;
        fixture.Transaction.FailCommit = !explicitRollback;

        // 실행
        var request = fixture.PatchAsync("{\"lastReadMessageId\":101}");
        await fixture.Transaction.WorkCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (explicitRollback)
        {
            fixture.Transaction.Rollback();
        }
        using var response = await request;
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        AssertError(response, json, HttpStatusCode.InternalServerError, "", "/api/v1/chat/rooms/41/read");
        Assert.DoesNotContain("Synthetic commit", json.GetRawText());
        Assert.True(fixture.Transaction.RolledBack);
        Assert.False(fixture.Transaction.Committed);
        Assert.Null(fixture.Transaction.CommittedCursor);
        Assert.Empty(fixture.Transaction.DeliveredPayloads);
        Assert.Equal(0, fixture.Transaction.PendingCount);
    }

    [Fact]
    public async Task Cancelled_http_request_propagates_before_commit_and_discards_intents()
    {
        // 준비
        await using var fixture = await ReadHttpFixture.StartAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Transaction.HoldCommit = true;

        // 실행
        var request = fixture.PatchAsync("{\"lastReadMessageId\":101}", cancellationToken: cancellation.Token);
        await fixture.Transaction.WorkCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await fixture.Transaction.RollbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 검증
        Assert.True(fixture.Transaction.RolledBack);
        Assert.False(fixture.Transaction.Committed);
        Assert.Empty(fixture.Transaction.DeliveredPayloads);
        Assert.Equal(0, fixture.Transaction.PendingCount);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized, "UNAUTHORIZED")]
    [InlineData("missing-id", HttpStatusCode.Forbidden, "ACCESS_DENIED")]
    [InlineData("not-numeric", HttpStatusCode.Forbidden, "ACCESS_DENIED")]
    public async Task Authentication_boundary_rejects_untrusted_or_unusable_principal(string? identity, HttpStatusCode status, string code)
    {
        // 준비
        await using var fixture = await ReadHttpFixture.StartAsync();

        // 실행
        using var response = await fixture.PatchAsync("{\"lastReadMessageId\":101}", identity: identity);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        AssertError(response, json, status, code, "/api/v1/chat/rooms/41/read");
        Assert.Equal(0, fixture.Transaction.ExecuteCount);
    }

    [Fact]
    public async Task Unconfigured_production_authentication_does_not_trust_bearer_or_user_input()
    {
        // 준비: 테스트 인증 scheme도 없는 운영 기본 구성이다.
        await using var fixture = await ReadHttpFixture.StartAsync(registerAuthentication: false);
        fixture.Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "synthetic.unsigned.token");

        // 실행
        using var response = await fixture.PatchAsync("{\"lastReadMessageId\":101,\"userId\":73}", "41");
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        AssertError(response, json, HttpStatusCode.Unauthorized, "UNAUTHORIZED", "/api/v1/chat/rooms/41/read");
        Assert.Equal(0, fixture.Transaction.ExecuteCount);
    }

    [Fact]
    public async Task Missing_storage_adapter_fails_closed_after_test_authentication()
    {
        // 준비
        await using var fixture = await ReadHttpFixture.StartAsync(registerTransaction: false);

        // 실행
        using var response = await fixture.PatchAsync("{\"lastReadMessageId\":101}");
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        AssertError(response, json, HttpStatusCode.ServiceUnavailable, "CHAT_READ_UNAVAILABLE", "/api/v1/chat/rooms/41/read");
        Assert.Equal(0, fixture.Transaction.ExecuteCount);
    }

    [Fact]
    public async Task Unsupported_media_type_is_rejected_before_application()
    {
        // 준비
        await using var fixture = await ReadHttpFixture.StartAsync();

        // 실행
        using var response = await fixture.PatchAsync("{\"lastReadMessageId\":101}", contentType: "text/plain");
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        AssertError(response, json, HttpStatusCode.InternalServerError, "", "/api/v1/chat/rooms/41/read");
        Assert.Equal(0, fixture.Transaction.ExecuteCount);
    }

    [Fact]
    public async Task Health_route_remains_public_and_read_route_rejects_wrong_method()
    {
        // 준비
        await using var fixture = await ReadHttpFixture.StartAsync(registerTransaction: false, registerAuthentication: false);

        // 실행
        using var health = await fixture.Client.GetAsync("/api/health");
        var healthJson = await ReadHttpFixture.ReadJsonAsync(health);
        using var wrongMethod = await fixture.Client.GetAsync("/api/v1/chat/rooms/41/read");

        // 검증
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        AssertFields(healthJson, "status", "service");
        Assert.Equal("UP", healthJson.GetProperty("status").GetString());
        Assert.Equal("TranslaCat.Chat", healthJson.GetProperty("service").GetString());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal(0, fixture.Transaction.ExecuteCount);
    }

    private static void AssertError(HttpResponseMessage response, JsonElement json, HttpStatusCode status, string code, string path)
    {
        Assert.Equal(status, response.StatusCode);
        AssertFields(json, "resultCode", "message", "body", "guid", "createDate");
        Assert.Equal((int)status, json.GetProperty("resultCode").GetInt32());
        Assert.StartsWith("Message <", json.GetProperty("message").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("guid").GetString()));
        AssertFields(json.GetProperty("body"), "errorCode", "path", "trace");
        Assert.Equal(code, json.GetProperty("body").GetProperty("errorCode").GetString());
        Assert.Equal(path, json.GetProperty("body").GetProperty("path").GetString());
        Assert.Equal("", json.GetProperty("body").GetProperty("trace").GetString());
    }

    internal static void AssertFields(JsonElement json, params string[] expected)
    {
        Assert.Equal(expected.Order(), json.EnumerateObject().Select(property => property.Name).Order());
    }
}
