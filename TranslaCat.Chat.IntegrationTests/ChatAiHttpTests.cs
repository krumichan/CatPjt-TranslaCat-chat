using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Infrastructure.Ai;
using TranslaCat.Chat.Infrastructure.Translation;

namespace TranslaCat.Chat.IntegrationTests;

public sealed class ChatAiHttpTests
{
    [Fact]
    public async Task Actual_HTTP_sends_complete_execution_request_and_normalizes_reply()
    {
        // 준비 — 실제 Provider가 아닌 이번 테스트의 loopback Kestrel만 사용한다.
        await using var stub = await ChatAiHttpStub.StartAsync(ChatAiHttpStub.ValidReplyAsync);
        using var http = Http();
        var client = Client(http, stub);

        // 실행
        var response = await client.GenerateAsync(Request(), default);

        // 검증
        Assert.Equal("request", response!.RequestId);
        Assert.True(response.ShouldRespond);
        var capture = Assert.Single(stub.Requests);
        Assert.Equal("POST", capture.Method);
        Assert.Equal("/internal/v1/model/execute", capture.Path);
        Assert.Equal("synthetic-ai-key", capture.ApiKey);
        using var json = JsonDocument.Parse(capture.Body);
        var body = json.RootElement;
        Assert.Equal("LUNA", body.GetProperty("tier").GetString());
        Assert.Equal("none", body.GetProperty("reasoningEffort").GetString());
        Assert.Equal(2048, body.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(1, body.GetProperty("maxProviderCalls").GetInt32());
        Assert.Equal("object", body.GetProperty("responseSchema").GetProperty("type").GetString());
        Assert.False(body.GetProperty("strict").GetBoolean());
        Assert.Contains("2026-09-26T12:00:00.123456", body.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Contains("member-1", body.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(422)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Technical_HTTP_failures_use_bounded_retry_without_leaking_response_details(int status)
    {
        // 준비
        await using var stub = await ChatAiHttpStub.StartAsync(async (context, _) =>
        {
            context.Response.StatusCode = status;
            await context.Response.WriteAsync("synthetic-private-detail");
        });
        using var http = Http();

        // 실행
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Client(http, stub).GenerateAsync(Request(), default));

        // 검증
        Assert.Equal(status is 400 or 422 ? 1 : 3, stub.Requests.Count);
        Assert.Equal("AI Server Chat Reply Error", failure.Message);
        if (status is 400 or 422)
        {
            Assert.False(Assert.IsType<ChatModelExecutionFailure>(failure.InnerException).Retryable);
        }
        Assert.DoesNotContain("synthetic-private-detail", failure.ToString());
        Assert.DoesNotContain("synthetic-ai-key", failure.ToString());
    }

    [Fact]
    public async Task Invalid_or_empty_execution_response_is_retried()
    {
        // 준비
        await using var malformed = await ChatAiHttpStub.StartAsync((context, _) => context.Response.WriteAsync("{invalid"));
        await using var empty = await ChatAiHttpStub.StartAsync((_, _) => Task.CompletedTask);
        using var http = Http();

        // 실행
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(http, malformed).GenerateAsync(Request(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(http, empty).GenerateAsync(Request(), default));

        // 검증
        Assert.Equal(3, malformed.Requests.Count);
        Assert.Equal(3, empty.Requests.Count);
    }

    [Theory]
    [InlineData(502, "REFUSAL", false, 1)]
    [InlineData(502, "OUTPUT_TOKEN_LIMIT", false, 1)]
    [InlineData(504, "PROVIDER_TIMEOUT", true, 3)]
    [InlineData(429, "PROVIDER_UNAVAILABLE", true, 1)]
    public async Task Execution_failure_preserves_safe_reason_and_bounded_retry(
        int status, string code, bool retryable, int expectedCalls)
    {
        // 준비 — 429의 Retry-After는 설정된 재시도 대기보다 길다.
        await using var stub = await ChatAiHttpStub.StartAsync((context, _) =>
        {
            context.Response.StatusCode = status;
            return context.Response.WriteAsJsonAsync(new
            {
                detail = new
                {
                    code,
                    retryable,
                    retryAfterSeconds = status == 429 ? 10 : 0
                },
                privateText = "synthetic-private-detail"
            });
        });
        using var http = Http();

        // 실행
        InvalidOperationException failure = status == 429
            ? await Assert.ThrowsAsync<ChatExecutionDeferredException>(() =>
                Client(http, stub).GenerateAsync(Request(), default))
            : await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Client(http, stub).GenerateAsync(Request(), default));

        // 검증
        var classified = Assert.IsType<ChatModelExecutionFailure>(failure.InnerException);
        Assert.Equal(code, classified.Code);
        Assert.Equal(retryable, classified.Retryable);
        Assert.Equal(expectedCalls, stub.Requests.Count);
        Assert.DoesNotContain("synthetic-private-detail", failure.ToString());
    }

    [Theory]
    [InlineData(300)]
    [InlineData(301)]
    [InlineData(600)]
    [InlineData(86400)]
    public async Task Long_retry_after_is_preserved_without_an_early_second_reply_request(int seconds)
    {
        // 준비: 대기 없이 끝나야 하는 긴 AI 기술 오류를 실제 loopback HTTP로 돌려준다.
        await using var stub = await ChatAiHttpStub.StartAsync((context, _) =>
        {
            context.Response.StatusCode = 503;
            return context.Response.WriteAsJsonAsync(new
            {
                detail = new
                {
                    code = "PROVIDER_UNAVAILABLE",
                    retryable = true,
                    retryAfterSeconds = seconds
                }
            });
        });
        using var http = Http();

        // 실행
        var failure = await Assert.ThrowsAsync<ChatExecutionDeferredException>(() =>
            Client(http, stub).GenerateAsync(Request(), default));

        // 검증: 전체 한 번, 실패 뒤 추가 HTTP 요청은 0회다.
        var classified = Assert.IsType<ChatModelExecutionFailure>(failure.InnerException);
        Assert.Equal(seconds, classified.RetryAfterSeconds);
        Assert.Equal(TimeSpan.FromSeconds(seconds), failure.RetryAfter);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Nonretryable_failure_ignores_a_long_retry_after_and_does_not_call_again()
    {
        // 준비: 긴 대기값이 있어도 AI가 재시도 불가라고 판정한 오류를 돌려준다.
        await using var stub = await ChatAiHttpStub.StartAsync((context, _) =>
        {
            context.Response.StatusCode = 503;
            return context.Response.WriteAsJsonAsync(new
            {
                detail = new
                {
                    code = "REFUSAL",
                    retryable = false,
                    retryAfterSeconds = 600
                }
            });
        });
        using var http = Http();

        // 실행
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(http, stub).GenerateAsync(Request(), default));

        // 검증: 대기 메타데이터는 파싱되지만 재시도나 예약으로 바뀌지 않는다.
        Assert.IsNotType<ChatExecutionDeferredException>(failure);
        Assert.False(Assert.IsType<ChatModelExecutionFailure>(failure.InnerException).Retryable);
        Assert.Single(stub.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("-1")]
    [InlineData("true")]
    [InlineData("\"600\"")]
    [InlineData("3.5")]
    [InlineData("86401")]
    [InlineData("2147483648")]
    public async Task Invalid_retry_after_is_not_confused_with_a_valid_long_wait(string? rawValue)
    {
        // 준비: 잘못된 값과 필드 누락을 실제 JSON 오류 응답으로 만든다.
        await using var stub = await ChatAiHttpStub.StartAsync((context, _) =>
        {
            context.Response.StatusCode = 503;
            context.Response.ContentType = "application/json";
            string field = rawValue is null ? "" : $",\"retryAfterSeconds\":{rawValue}";
            return context.Response.WriteAsync(
                $"{{\"detail\":{{\"code\":\"PROVIDER_UNAVAILABLE\",\"retryable\":true{field}}}}}");
        });
        using var http = Http();

        // 실행
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(http, stub).GenerateAsync(Request(), default));

        // 검증: invalid는 대기 지시가 아니며 기존 최대 시도 안에서만 재시도한다.
        Assert.Null(Assert.IsType<ChatModelExecutionFailure>(failure.InnerException).RetryAfterSeconds);
        Assert.Equal(3, stub.Requests.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Allowed_short_retry_after_retries_once_and_preserves_the_call_limit(int seconds)
    {
        // 준비: 첫 번째 실패만 짧은 지시를 전달하고 두 번째 호출에 정상 응답한다.
        int requestNumber = 0;
        await using var stub = await ChatAiHttpStub.StartAsync((context, body) =>
        {
            if (Interlocked.Increment(ref requestNumber) == 1)
            {
                context.Response.StatusCode = 503;
                return context.Response.WriteAsJsonAsync(new
                {
                    detail = new
                    {
                        code = "PROVIDER_UNAVAILABLE",
                        retryable = true,
                        retryAfterSeconds = seconds
                    }
                });
            }

            return ChatAiHttpStub.ValidReplyAsync(context, body);
        });
        using var http = Http();
        var client = new HttpChatAiReplyClient(http, Options(stub, TimeSpan.FromSeconds(seconds)), new(TimeProvider.System));

        // 실행
        var result = await client.GenerateAsync(Request(), default);

        // 검증: 지시된 짧은 대기 뒤 성공하며 HTTP 호출은 정확히 두 번이다.
        Assert.True(result!.ShouldRespond);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Caller_cancellation_during_HTTP_stops_without_retry()
    {
        // 준비
        await using var stub = await ChatAiHttpStub.StartAsync((context, _) => Task.Delay(Timeout.Infinite, context.RequestAborted));
        using var http = Http();
        using var cancellation = new CancellationTokenSource();

        // 실행
        var pending = Client(http, stub).GenerateAsync(Request(), cancellation.Token);
        await stub.FirstRequest.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        // 검증
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_during_reply_retry_wait_prevents_another_HTTP_attempt()
    {
        // 준비: 첫 실패 후 재시도 대기 중인 답변 클라이언트를 취소한다.
        await using var stub = await ChatAiHttpStub.StartAsync((context, _) =>
        {
            context.Response.StatusCode = 503;
            return context.Response.WriteAsync("synthetic-unavailable");
        });
        using var http = Http();
        var client = new HttpChatAiReplyClient(http, Options(stub, TimeSpan.FromSeconds(5)), new(TimeProvider.System));
        using var cancellation = new CancellationTokenSource();

        // 실행: 첫 응답 이후 대기 구간에서 취소하고 긴 대기를 기다리지 않는다.
        var pending = client.GenerateAsync(Request(), cancellation.Token);
        await stub.FirstRequest.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        cancellation.Cancel();
        var failure = await Record.ExceptionAsync(() => pending);

        // 검증: 취소가 전파되고 추가 HTTP 요청은 없다.
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Per_attempt_timeout_is_retried_and_shared_circuit_blocks_later_AI_calls()
    {
        // 준비
        await using var stalled = await ChatAiHttpStub.StartAsync((context, _) => Task.Delay(Timeout.Infinite, context.RequestAborted));
        using var http = Http();
        var timed = new HttpChatAiReplyClient(http, TimeoutOptions(), new(TimeProvider.System));

        // 실행
        await Assert.ThrowsAsync<InvalidOperationException>(() => timed.GenerateAsync(Request(), default));

        // 검증
        Assert.Equal(3, stalled.Requests.Count);

        // 실행 — 같은 실제 HTTP client/circuit에 10회 실패가 쌓이면 다음 재시도는 전송 없이 거절된다.
        await using var failing = await ChatAiHttpStub.StartAsync((context, _) =>
        {
            context.Response.StatusCode = 503;
            return Task.CompletedTask;
        });
        var circuit = new ChatExternalApiCircuitBreaker(TimeProvider.System);
        var client = new HttpChatAiReplyClient(http, Options(failing), circuit);
        for (int i = 0; i < 4; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(Request(), default));
        }

        Assert.Equal(10, failing.Requests.Count);

        ChatAiOptions TimeoutOptions()
        {
            return new()
            {
                Enabled = true,
                AiBaseUri = stalled.Address,
                ApiKey = "synthetic-ai-key",
                AttemptTimeout = TimeSpan.FromMilliseconds(100),
                ConnectTimeout = TimeSpan.FromMilliseconds(100),
                RetryWait = TimeSpan.Zero
            };
        }
    }

    internal static ChatAiReplyRequest Request()
    {
        return new("request", "MENTION", new(1, "GROUP", "synthetic", null),
        new(2, "Mika", null, "synthetic persona", "ja"),
        new(3, "member-1", "synthetic", "@Mika synthetic", ChatMySqlFixture.Epoch.AddTicks(1234567)), [], 30, 12000, 800);
    }

    private static HttpClient Http()
    {
        return new(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static ChatAiOptions Options(ChatAiHttpStub stub, TimeSpan? retryWait = null)
    {
        return new()
        {
            Enabled = true,
            AiBaseUri = stub.Address,
            ApiKey = "synthetic-ai-key",
            RetryWait = retryWait ?? TimeSpan.Zero,
            AttemptTimeout = TimeSpan.FromSeconds(5),
            ConnectTimeout = TimeSpan.FromSeconds(1)
        };
    }

    private static HttpChatAiReplyClient Client(HttpClient http, ChatAiHttpStub stub)
    {
        return new(http, Options(stub), new(TimeProvider.System));
    }
}

internal sealed class ChatAiHttpStub(WebApplication application) : IAsyncDisposable
{
    private readonly TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int count;
    internal Uri Address { get; private set; } = null!;
    internal ConcurrentQueue<Capture> Requests { get; } = new();
    internal Task FirstRequest => first.Task;
    internal sealed record Capture(string Method, string Path, string ApiKey, string Body);

    internal static async Task<ChatAiHttpStub> StartAsync(Func<HttpContext, string, Task> respond)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var stub = new ChatAiHttpStub(app);
        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            string body = await reader.ReadToEndAsync(context.RequestAborted);
            Interlocked.Increment(ref stub.count);
            stub.Requests.Enqueue(new(context.Request.Method, context.Request.Path, context.Request.Headers["X-API-KEY"].ToString(), body));
            stub.first.TrySetResult();
            try
            {
                await respond(context, body);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // 합성 client 취소/timeout으로 종료된 요청만 흡수한다.
            }
        });
        await app.StartAsync();
        stub.Address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        return stub;
    }

    internal static Task ValidReplyAsync(HttpContext context, string body)
    {
        using var json = JsonDocument.Parse(body);
        return context.Response.WriteAsJsonAsync(new
        {
            output = new
            {
                shouldRespond = true,
                reply = "合成応答",
                languageCode = "ja"
            },
            inputTokens = 12,
            outputTokens = 4,
            provider = "fake",
            model = "synthetic",
            providerCalls = 1
        });
    }

    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await application.StopAsync(deadline.Token);
        await application.DisposeAsync();
    }
}
