using System.Net;
using System.Text;
using System.Text.Json;
using TranslaCat.Chat.Infrastructure.Push;

namespace TranslaCat.Chat.ApiTests.Push;

public sealed class ExpoChatPushClientTests
{
    private const string TicketId = "d8c50804-e568-4cf6-a7ab-9d8df68c48e0";
    private static readonly ExpoPushPayload Payload = new(
        "ExponentPushToken[synthetic-only]",
        "TranslaCat",
        "새 메시지가 도착했습니다.",
        "chat-messages",
        "default",
        new ExpoPushData(1, "event-1", "41", "73", "binding-1"));

    [Fact]
    public async Task Send_posts_one_camelcase_payload_to_fixed_https_endpoint_with_optional_bearer()
    {
        // 준비
        string? requestBody = null;
        var handler = new FakeHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://exp.host/--/api/v2/push/send", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("synthetic-access", request.Headers.Authorization.Parameter);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "application/json");
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return JsonResponse("{\"data\":[{\"status\":\"ok\",\"id\":\"" + TicketId + "\"}]}");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://ignored.example.invalid/") };
        var client = new ExpoChatPushClient(http, Options(accessToken: "synthetic-access"));

        // 실행
        ExpoPushResult result = await client.SendAsync(Payload, default);

        // 검증
        Assert.Equal(new ExpoPushResult("ok", TicketId), result);
        using var json = JsonDocument.Parse(requestBody!);
        Assert.Equal(1, json.RootElement.GetArrayLength());
        JsonElement message = json.RootElement[0];
        Assert.Equal(6, message.EnumerateObject().Count());
        Assert.Equal(Payload.To, message.GetProperty("to").GetString());
        Assert.Equal(Payload.Title, message.GetProperty("title").GetString());
        Assert.Equal(Payload.Body, message.GetProperty("body").GetString());
        Assert.Equal(Payload.ChannelId, message.GetProperty("channelId").GetString());
        Assert.Equal(Payload.Sound, message.GetProperty("sound").GetString());
        Assert.False(message.TryGetProperty("To", out _));
        Assert.Equal(5, message.GetProperty("data").EnumerateObject().Count());
        Assert.Equal(1, message.GetProperty("data").GetProperty("schemaVersion").GetInt32());
        Assert.Equal("binding-1", message.GetProperty("data").GetProperty("accountBindingId").GetString());
        Assert.Equal(1, handler.Hits);
    }

    [Fact]
    public void Payload_serialization_exposes_only_the_versioned_navigation_contract()
    {
        // 준비
        string[] expectedFields = ["schemaVersion", "eventId", "roomId", "messageId", "accountBindingId"];

        // 실행
        using var json = JsonDocument.Parse(ExpoChatPushClient.SerializePayload(Payload));

        // 검증
        Assert.Equal(expectedFields, json.RootElement.GetProperty("data").EnumerateObject().Select(property => property.Name));
        Assert.Equal("event-1", json.RootElement.GetProperty("data").GetProperty("eventId").GetString());
        Assert.Equal("41", json.RootElement.GetProperty("data").GetProperty("roomId").GetString());
        Assert.Equal("73", json.RootElement.GetProperty("data").GetProperty("messageId").GetString());
    }

    [Theory]
    [InlineData("{\"status\":\"ok\"}", "ok", null)]
    [InlineData("{\"status\":\"error\",\"message\":\"sensitive provider text\",\"details\":{\"error\":\"DeviceNotRegistered\"}}", "error", "DeviceNotRegistered")]
    public async Task Receipt_posts_one_id_and_reads_only_its_result(string receipt, string status, string? error)
    {
        // 준비
        var handler = new FakeHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("https://exp.host/--/api/v2/push/getReceipts", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Single(body.RootElement.EnumerateObject());
            Assert.Equal(1, body.RootElement.GetProperty("ids").GetArrayLength());
            Assert.Equal(TicketId, body.RootElement.GetProperty("ids")[0].GetString());
            return JsonResponse("{\"data\":{\"" + TicketId + "\":" + receipt + "}}");
        });
        using var http = new HttpClient(handler);
        var client = new ExpoChatPushClient(http, Options());

        // 실행
        ExpoPushResult result = await client.ReceiptAsync(TicketId, default);

        // 검증
        Assert.Equal(status, result.Status);
        Assert.Equal(error, result.Error);
        Assert.Equal(status == "ok" ? TicketId : null, result.TicketId);
        Assert.DoesNotContain("sensitive", result.ToString());
        Assert.Equal(1, handler.Hits);
    }

    [Fact]
    public async Task Receipt_absent_from_valid_mapping_remains_pending()
    {
        // 준비
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse("{\"data\":{}}"))));
        var client = new ExpoChatPushClient(http, Options());

        // 실행
        ExpoPushResult result = await client.ReceiptAsync(TicketId, default);

        // 검증
        Assert.Equal(new ExpoPushResult("pending", TicketId), result);
    }

    [Theory]
    [InlineData(429, "retry")]
    [InlineData(500, "retry")]
    [InlineData(503, "retry")]
    [InlineData(400, "permanent")]
    [InlineData(401, "permanent")]
    [InlineData(403, "permanent")]
    [InlineData(307, "permanent")]
    public async Task Http_failure_is_classified_without_returning_provider_body(int statusCode, string error)
    {
        // 준비
        var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse("sensitive-provider-body", (HttpStatusCode)statusCode)));
        using var http = new HttpClient(handler);
        var client = new ExpoChatPushClient(http, Options());

        // 실행
        ExpoPushResult result = await client.SendAsync(Payload, default);

        // 검증
        Assert.Equal(new ExpoPushResult("error", Error: error), result);
        Assert.DoesNotContain("sensitive", result.ToString());
        Assert.Equal(1, handler.Hits);
    }

    [Theory]
    [InlineData("DeviceNotRegistered", "DeviceNotRegistered")]
    [InlineData("MessageTooBig", "MessageTooBig")]
    [InlineData("MessageRateExceeded", "MessageRateExceeded")]
    [InlineData("MismatchSenderId", "MismatchSenderId")]
    [InlineData("InvalidCredentials", "InvalidCredentials")]
    [InlineData("sensitive-provider-error", "permanent")]
    public async Task Ticket_returns_only_allowlisted_error_codes(string providerError, string expectedError)
    {
        // 준비
        string body = "{\"data\":[{\"status\":\"error\",\"message\":\"sensitive-provider-message\",\"details\":{\"error\":\"" + providerError + "\",\"providerDetail\":\"sensitive-detail\"}}]}";
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse(body))));
        var client = new ExpoChatPushClient(http, Options());

        // 실행
        ExpoPushResult result = await client.SendAsync(Payload, default);

        // 검증
        Assert.Equal(new ExpoPushResult("error", Error: expectedError), result);
        Assert.DoesNotContain("sensitive", result.ToString());
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":[{\"status\":\"ok\",\"id\":\"id-1\"},{\"status\":\"ok\",\"id\":\"id-2\"}]}")]
    [InlineData("{\"data\":[{\"status\":\"ok\"}]}")]
    [InlineData("{\"data\":[{\"status\":\"ok\",\"id\":\"\"}]}")]
    [InlineData("{\"data\":[{\"status\":\"ok\",\"id\":\"with space\"}]}")]
    [InlineData("{\"data\":[{\"status\":\"ok\",\"id\":42}]}")]
    [InlineData("{\"data\":[{\"status\":\"pending\",\"id\":\"id-1\"}]}")]
    [InlineData("{\"data\":[{\"status\":\"OK\",\"id\":\"id-1\"}]}")]
    [InlineData("{\"data\":[{\"status\":false,\"id\":\"id-1\"}]}")]
    [InlineData("{\"data\":[{\"status\":\"ok\",\"status\":\"error\",\"id\":\"id-1\"}]}")]
    [InlineData("{\"data\":[{\"status\":\"ok\",\"id\":\"id-1\",\"id\":\"id-2\"}]}")]
    [InlineData("{\"data\":[],\"data\":[{\"status\":\"ok\",\"id\":\"id-1\"}]}")]
    [InlineData("{\"data\":[{\"status\":\"error\",\"details\":{\"error\":\"DeviceNotRegistered\",\"error\":\"InvalidCredentials\"}}]}")]
    [InlineData("{\"data\":[{\"status\":\"error\",\"details\":{\"error\":3}}]}")]
    [InlineData("{\"data\":[{\"status\":\"error\",\"id\":\"id-1\"}]}")]
    [InlineData("{\"data\":[{\"status\":\"error\",\"message\":{}}]}")]
    [InlineData("{\"data\":[{\"status\":\"error\",\"details\":null}]}")]
    [InlineData("{\"data\":[{\"status\":\"ok\",\"id\":\"id-1\",\"extra\":true}]}")]
    [InlineData("{\"data\":[{\"status\":\"ok\",\"id\":\"id-1\",\"details\":{\"error\":\"DeviceNotRegistered\"}}]}")]
    [InlineData("{\"data\":[{\"status\":\"ok\",\"id\":\"id-1\"}],\"errors\":[{\"code\":\"sensitive\"}]}")]
    [InlineData("{\"data\":[{\"status\":\"ok\",\"id\":\"id-1\"}],\"extra\":true}")]
    [InlineData("{\"data\":[{\"status\":\"ok\",\"id\":\"id-1\"}],}")]
    public async Task Malformed_or_ambiguous_ticket_response_fails_closed(string body)
    {
        // 준비
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse(body))));
        var client = new ExpoChatPushClient(http, Options());

        // 실행
        ExpoPushResult result = await client.SendAsync(Payload, default);

        // 검증
        Assert.Equal(new ExpoPushResult("error", Error: "invalid_response"), result);
    }

    [Theory]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":{\"different-id\":{\"status\":\"ok\"}}}")]
    [InlineData("{\"data\":{\"ID\":{\"status\":\"ok\"},\"ID\":{\"status\":\"error\"}}}")]
    [InlineData("{\"data\":{\"ID\":{\"status\":\"ok\",\"id\":\"ID\"}}}")]
    [InlineData("{\"data\":{\"ID\":{\"status\":\"pending\"}}}")]
    public async Task Receipt_rejects_mismatched_duplicate_or_invalid_status(string body)
    {
        // 준비
        body = body.Replace("ID", TicketId, StringComparison.Ordinal);
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse(body))));
        var client = new ExpoChatPushClient(http, Options());

        // 실행
        ExpoPushResult result = await client.ReceiptAsync(TicketId, default);

        // 검증
        Assert.Equal(new ExpoPushResult("error", Error: "invalid_response"), result);
    }

    [Fact]
    public async Task Invalid_receipt_id_does_not_make_an_http_request()
    {
        // 준비
        var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Unexpected request."));
        using var http = new HttpClient(handler);
        var client = new ExpoChatPushClient(http, Options());

        // 실행
        string[] invalidIds = ["", " ", "with space", "line\r\nbreak", new string('a', 129), "non-ascii-한"];
        ExpoPushResult[] results = await Task.WhenAll(invalidIds.Select(id => client.ReceiptAsync(id, default)));

        // 검증
        Assert.All(results, result => Assert.Equal(new ExpoPushResult("error", Error: "permanent"), result));
        Assert.Equal(0, handler.Hits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_response_with_or_without_length_is_rejected(bool unknownLength)
    {
        // 준비
        var handler = new FakeHandler((_, _) =>
        {
            byte[] bytes = Encoding.UTF8.GetBytes(new string('x', 129));
            HttpContent content = unknownLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        var client = new ExpoChatPushClient(http, Options(maxResponseBytes: 128));

        // 실행
        ExpoPushResult result = await client.SendAsync(Payload, default);

        // 검증
        Assert.Equal(new ExpoPushResult("error", Error: "invalid_response"), result);
    }

    [Fact]
    public async Task Excessively_nested_response_is_rejected()
    {
        // 준비
        string nested = new string('[', 15) + "0" + new string(']', 15);
        string body = "{\"data\":[{\"status\":\"error\",\"details\":{\"nested\":" + nested + "}}]}";
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse(body))));
        var client = new ExpoChatPushClient(http, Options());

        // 실행
        ExpoPushResult result = await client.SendAsync(Payload, default);

        // 검증
        Assert.Equal(new ExpoPushResult("error", Error: "invalid_response"), result);
    }

    [Fact]
    public async Task Network_failure_is_retryable_without_exposing_exception_details()
    {
        // 준비
        using var http = new HttpClient(new FakeHandler((_, _) => throw new HttpRequestException("sensitive-token-provider-error")));
        var client = new ExpoChatPushClient(http, Options());

        // 실행
        ExpoPushResult result = await client.SendAsync(Payload, default);

        // 검증
        Assert.Equal(new ExpoPushResult("error", Error: "retry"), result);
        Assert.DoesNotContain("sensitive", result.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Own_deadline_covers_both_response_headers_and_body(bool stallBody)
    {
        // 준비
        var handler = new FakeHandler(async (_, token) =>
        {
            if (!stallBody)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StallingContent() };
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new ExpoChatPushClient(http, Options(timeoutSeconds: 1));

        // 실행
        ExpoPushResult result = await client.SendAsync(Payload, default);

        // 검증
        Assert.Equal(new ExpoPushResult("error", Error: "retry"), result);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_without_provider_exception_details()
    {
        // 준비
        using var cancellation = new CancellationTokenSource();
        var handler = new FakeHandler((_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException("sensitive-provider-error", new Exception("sensitive-inner"), cancellation.Token);
        });
        using var http = new HttpClient(handler);
        var client = new ExpoChatPushClient(http, Options());

        // 실행
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(Payload, cancellation.Token));

        // 검증
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("sensitive", error.ToString());
    }

    private static ChatPushOptions Options(int timeoutSeconds = 10, int maxResponseBytes = 16384, string? accessToken = null)
    {
        return new ChatPushOptions
        {
            RequestTimeoutSeconds = timeoutSeconds,
            MaxResponseBytes = maxResponseBytes,
            ExpoAccessToken = accessToken
        };
    }

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Hits { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hits++;
            return respond(request, cancellationToken);
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : ByteArrayContent(bytes)
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StallingContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<Stream>(new StallingStream());
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            throw new NotSupportedException();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
