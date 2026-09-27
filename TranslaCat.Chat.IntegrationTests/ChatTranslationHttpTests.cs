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
using TranslaCat.Chat.Application.Translation;
using TranslaCat.Chat.Infrastructure.Translation;

namespace TranslaCat.Chat.IntegrationTests;

public sealed class ChatTranslationHttpTests
{
    private const string SyntheticApiKey = "synthetic-translation-key-never-a-provider-credential";
    private const string SyntheticFailureBody = "synthetic-upstream-details-must-not-escape";

    [Fact]
    public async Task Actual_http_request_preserves_AI_contract_and_trims_the_translated_text()
    {
        // 준비: 실제 AI 주소를 읽지 않고 이번 시험의 loopback Kestrel만 사용한다.
        await using var server = await TranslationStub.StartAsync((context, _) =>
            RespondAsync(context, "  合成翻訳\n"));
        using var http = CreateHttpClient();
        var client = new HttpChatTranslationClient(http, Options(server));

        // 실행
        var result = await client.TranslateAsync("합성 원문 😀", "ja", CancellationToken.None);

        // 검증: routing, 실제 header/body 직렬화와 응답 역직렬화를 모두 통과했다.
        Assert.Equal("合成翻訳", result);
        var request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/internal/v1/model/execute", request.Path);
        Assert.Equal(SyntheticApiKey, request.ApiKey);
        Assert.StartsWith("application/json", request.ContentType);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal("LUNA", json.RootElement.GetProperty("tier").GetString());
        Assert.Equal("none", json.RootElement.GetProperty("reasoningEffort").GetString());
        Assert.Equal(1024, json.RootElement.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("maxProviderCalls").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("responseSchema").ValueKind);
        Assert.Contains("합성 원문 😀", json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Contains("Target language code: ja", json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Transient_HTTP_failure_retries_and_returns_the_later_success(int status)
    {
        // 준비
        await using var server = await TranslationStub.StartAsync(async (context, number) =>
        {
            if (number < 3)
            {
                context.Response.StatusCode = status;
                await context.Response.WriteAsync(SyntheticFailureBody);
                return;
            }

            await RespondAsync(context, "recovered");
        });
        using var http = CreateHttpClient();
        var client = new HttpChatTranslationClient(http, Options(server));

        // 실행
        var result = await client.TranslateAsync("synthetic", "en", CancellationToken.None);

        // 검증: 새 HTTP 요청 세 개가 동일한 원문/언어를 전달했다.
        Assert.Equal("recovered", result);
        Assert.Equal(3, server.Requests.Count);
        Assert.Single(server.Requests.Select(request => request.Body).Distinct());
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Repeated_transient_HTTP_failure_stops_at_the_configured_attempt_limit(int status)
    {
        // 준비
        await using var server = await TranslationStub.StartAsync((context, _) => FailureAsync(context, status));
        using var http = CreateHttpClient();
        var client = new HttpChatTranslationClient(http, Options(server));

        // 실행
        var failure = await Record.ExceptionAsync(() => client.TranslateAsync("synthetic", "en", CancellationToken.None));

        // 검증
        AssertSanitizedFailure(failure);
        Assert.Equal(3, server.Requests.Count);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(301)]
    [InlineData(600)]
    [InlineData(86400)]
    public async Task Long_retry_after_is_preserved_without_an_early_second_translation_request(int seconds)
    {
        // 준비: Provider 대기 지시를 실제 loopback HTTP 오류 본문으로 전달한다.
        await using var server = await TranslationStub.StartAsync((context, _) =>
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
        using var http = CreateHttpClient();
        var client = new HttpChatTranslationClient(http, Options(server));

        // 실행
        var failure = await Assert.ThrowsAsync<ChatExecutionDeferredException>(() =>
            client.TranslateAsync("synthetic", "en", default));

        // 검증: 첫 요청의 메타데이터는 보존하고 추가 요청은 보내지 않는다.
        var classified = Assert.IsType<TranslaCat.Chat.Infrastructure.Ai.ChatModelExecutionFailure>(failure.InnerException);
        Assert.Equal(seconds, classified.RetryAfterSeconds);
        Assert.Equal(TimeSpan.FromSeconds(seconds), failure.RetryAfter);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Nonretryable_translation_failure_ignores_a_long_retry_after()
    {
        // 준비: 긴 대기값과 재시도 불가 판정을 함께 반환한다.
        await using var server = await TranslationStub.StartAsync((context, _) =>
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
        using var http = CreateHttpClient();
        var client = new HttpChatTranslationClient(http, Options(server));

        // 실행
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.TranslateAsync("synthetic", "en", default));

        // 검증: 재시도 불가 오류는 예약하지 않고 첫 HTTP 요청으로 끝난다.
        Assert.IsNotType<ChatExecutionDeferredException>(failure);
        Assert.False(Assert.IsType<TranslaCat.Chat.Infrastructure.Ai.ChatModelExecutionFailure>(failure.InnerException).Retryable);
        Assert.Single(server.Requests);
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
    public async Task Invalid_retry_after_does_not_create_an_unbounded_translation_wait(string? rawValue)
    {
        // 준비: 필드 누락과 잘못된 JSON 값은 AI의 유효한 대기 지시가 아니다.
        await using var server = await TranslationStub.StartAsync((context, _) =>
        {
            context.Response.StatusCode = 503;
            context.Response.ContentType = "application/json";
            string field = rawValue is null ? "" : $",\"retryAfterSeconds\":{rawValue}";
            return context.Response.WriteAsync(
                $"{{\"detail\":{{\"code\":\"PROVIDER_UNAVAILABLE\",\"retryable\":true{field}}}}}");
        });
        using var http = CreateHttpClient();
        var client = new HttpChatTranslationClient(http, Options(server));

        // 실행
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.TranslateAsync("synthetic", "en", default));

        // 검증: 기존 최대 시도 세 번만 사용하고 임의 대기하지 않는다.
        var classified = Assert.IsType<TranslaCat.Chat.Infrastructure.Ai.ChatModelExecutionFailure>(failure.InnerException);
        Assert.Null(classified.RetryAfterSeconds);
        Assert.Equal(3, server.Requests.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Allowed_short_retry_after_retries_translation_once(int seconds)
    {
        // 준비: 첫 실패만 짧은 지시를 주고 두 번째 요청은 성공시킨다.
        await using var server = await TranslationStub.StartAsync((context, number) =>
        {
            if (number == 1)
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

            return RespondAsync(context, "recovered");
        });
        using var http = CreateHttpClient();
        var client = new HttpChatTranslationClient(http,
            Options(server, attempts: 2, retryWait: TimeSpan.FromSeconds(seconds)));

        // 실행
        var result = await client.TranslateAsync("synthetic", "en", default);

        // 검증: 번역은 성공하고 실제 HTTP는 두 번만 발생한다.
        Assert.Equal("recovered", result);
        Assert.Equal(2, server.Requests.Count);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task Invalid_or_unauthorized_HTTP_4xx_fails_without_repeating_provider_calls(int status)
    {
        // 준비
        await using var server = await TranslationStub.StartAsync((context, _) => FailureAsync(context, status));
        using var http = CreateHttpClient();
        var client = new HttpChatTranslationClient(http, Options(server));

        // 실행
        var failure = await Record.ExceptionAsync(() => client.TranslateAsync("synthetic", "en", CancellationToken.None));

        // 검증
        AssertSanitizedFailure(failure);
        Assert.Single(server.Requests);
        Assert.False(Assert.IsType<TranslaCat.Chat.Infrastructure.Ai.ChatModelExecutionFailure>(failure!.InnerException).Retryable);
    }

    [Theory]
    [InlineData("{ malformed synthetic JSON")]
    [InlineData("{\"output\":{},\"providerCalls\":1}")]
    public async Task Legacy_post_retries_invalid_success_JSON(string body)
    {
        // 준비
        await using var server = await TranslationStub.StartAsync((context, _) =>
        {
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(body);
        });
        using var http = CreateHttpClient();
        var client = new HttpChatTranslationClient(http, Options(server));

        // 실행
        var failure = await Record.ExceptionAsync(() => client.TranslateAsync("synthetic", "en", CancellationToken.None));

        // 검증
        AssertSanitizedFailure(failure);
        Assert.Equal(3, server.Requests.Count);
    }

    [Theory]
    [InlineData("{\"output\":\"   \",\"providerCalls\":1}")]
    public async Task Missing_or_blank_translation_is_not_a_success_and_is_not_retried(string body)
    {
        // 준비
        await using var server = await TranslationStub.StartAsync((context, _) =>
        {
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(body);
        });
        using var http = CreateHttpClient();
        var client = new HttpChatTranslationClient(http, Options(server));

        // 실행
        var failure = await Record.ExceptionAsync(() => client.TranslateAsync("synthetic", "en", CancellationToken.None));

        // 검증
        Assert.NotNull(failure);
        Assert.Equal("AI translation response is empty.", failure.Message);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("17")]
    [InlineData("true")]
    [InlineData("false")]
    public async Task Non_string_execution_output_is_retried_as_protocol_error(string scalar)
    {
        // 준비
        await using var server = await TranslationStub.StartAsync((context, _) =>
        {
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync("{\"output\":" + scalar + ",\"providerCalls\":1}");
        });
        using var http = CreateHttpClient();
        var client = new HttpChatTranslationClient(http, Options(server));

        // 실행
        var failure = await Record.ExceptionAsync(() => client.TranslateAsync("synthetic", "en", default));

        // 검증
        AssertSanitizedFailure(failure);
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public async Task Shared_circuit_counts_each_actual_retry_blocks_http_and_recovers_after_half_open_ten_successes()
    {
        // 준비 — HTTP 요청은 실제 loopback, 회로의 시간만 고정한다.
        var clock = new ManualTranslationClock();
        var circuit = new ChatExternalApiCircuitBreaker(clock);
        bool failing = true;
        await using var server = await TranslationStub.StartAsync((context, _) => failing
            ? FailureAsync(context, 503) : RespondAsync(context, "recovered"));
        using var http = CreateHttpClient();
        var first = new HttpChatTranslationClient(http, Options(server), circuit);
        var second = new HttpChatTranslationClient(http, Options(server), circuit);

        // 실행 — 4개 논리 호출 중 실제 HTTP10번째 실패에서 OPEN, 나머지 시도는 회로에서 거절한다.
        for (int index = 0; index < 4; index++)
        {
            AssertSanitizedFailure(await Record.ExceptionAsync(() => first.TranslateAsync("synthetic", "en", default)));
        }
        AssertSanitizedFailure(await Record.ExceptionAsync(() => second.TranslateAsync("synthetic", "en", default)));
        Assert.Equal(10, server.Requests.Count);

        clock.Advance(TimeSpan.FromSeconds(10));
        failing = false;
        for (int index = 0; index < 10; index++)
        {
            Assert.Equal("recovered", await second.TranslateAsync("synthetic", "en", default));
        }

        // 검증 — 성공10개 이후 CLOSED에서 다음 호출도 실제 서버에 도달한다.
        Assert.Equal("recovered", await first.TranslateAsync("synthetic", "en", default));
        Assert.Equal(21, server.Requests.Count);
    }

    [Fact]
    public async Task Blank_translation_is_outside_circuit_failure_metrics()
    {
        // 준비
        var circuit = new ChatExternalApiCircuitBreaker(new ManualTranslationClock());
        await using var server = await TranslationStub.StartAsync((context, _) =>
            RespondAsync(context, " "));
        using var http = CreateHttpClient();
        var client = new HttpChatTranslationClient(http, Options(server), circuit);

        // 실행
        for (int index = 0; index < 11; index++)
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => client.TranslateAsync("synthetic", "en", default));
            Assert.Equal("AI translation response is empty.", failure.Message);
        }

        // 검증 — HTTP decode 성공으로 기록되어 회로는 열리지 않고 요청마다 한 번 호출된다.
        Assert.Equal(11, server.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Attempt_timeout_bounds_both_response_headers_and_body_and_retries(bool sendHeaders)
    {
        // 준비: 외부 caller 취소 없이 adapter의 attempt timeout이 실제 socket 대기를 종료해야 한다.
        await using var server = await TranslationStub.StartAsync(async (context, _) =>
        {
            if (sendHeaders)
            {
                context.Response.ContentType = "application/json";
                await context.Response.StartAsync();
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
        });
        using var http = CreateHttpClient();
        var client = new HttpChatTranslationClient(http, Options(server, attempts: 2,
            attemptTimeout: TimeSpan.FromMilliseconds(500)));
        using var safetyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // 실행
        var failure = await Record.ExceptionAsync(() => client.TranslateAsync("synthetic", "en", safetyDeadline.Token));

        // 검증: 테스트 safety deadline이 아니라 각 attempt timeout으로 실패해야 한다.
        Assert.False(safetyDeadline.IsCancellationRequested);
        AssertSanitizedFailure(failure);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task Caller_cancellation_during_HTTP_is_propagated_without_retry()
    {
        // 준비
        await using var server = await TranslationStub.StartAsync((context, _) =>
            Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted));
        using var http = CreateHttpClient();
        var client = new HttpChatTranslationClient(http, Options(server));
        using var cancellation = new CancellationTokenSource();

        // 실행: 요청이 실제 서버에 도착한 뒤 취소하여 사전 취소만 검증하는 것을 피한다.
        var pending = client.TranslateAsync("synthetic", "en", cancellation.Token);
        await server.FirstRequest.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var failure = await Record.ExceptionAsync(() => pending);

        // 검증
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Caller_cancellation_during_retry_wait_prevents_another_HTTP_attempt()
    {
        // 준비
        await using var server = await TranslationStub.StartAsync((context, _) => FailureAsync(context, 503));
        using var http = CreateHttpClient();
        var client = new HttpChatTranslationClient(http, Options(server, retryWait: TimeSpan.FromSeconds(5)));
        using var cancellation = new CancellationTokenSource();

        // 실행: 실제 실패 응답 송신이 끝난 뒤 retry 대기 구간에서 취소한다.
        var pending = client.TranslateAsync("synthetic", "en", cancellation.Token);
        await server.FirstResponse.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        cancellation.Cancel();
        var failure = await Record.ExceptionAsync(() => pending);

        // 검증
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Single(server.Requests);
    }

    private static HttpClient CreateHttpClient()
    {
        return new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(1)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static ChatTranslationOptions Options(TranslationStub server, int attempts = 3,
        TimeSpan? attemptTimeout = null, TimeSpan? retryWait = null)
    {
        return new()
        {
            Enabled = true,
            AiBaseUri = server.Address,
            ApiKey = SyntheticApiKey,
            AttemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(3),
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            MaximumAttempts = attempts,
            RetryWait = retryWait ?? TimeSpan.FromMilliseconds(10),
            LeaseDuration = TimeSpan.FromSeconds(30)
        };
    }

    private static Task FailureAsync(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsync(SyntheticFailureBody);
    }

    private static Task RespondAsync(HttpContext context, string output)
    {
        return context.Response.WriteAsJsonAsync(new
        {
            output,
            inputTokens = 1,
            outputTokens = 1,
            provider = "fake",
            model = "synthetic",
            providerCalls = 1
        });
    }

    private static void AssertSanitizedFailure(Exception? failure)
    {
        Assert.NotNull(failure);
        Assert.Equal("AI Server Chat Translation Error", failure.Message);
        Assert.DoesNotContain(SyntheticApiKey, failure.ToString());
        Assert.DoesNotContain(SyntheticFailureBody, failure.ToString());
    }

    private sealed record CapturedRequest(string Method, string Path, string ApiKey, string ContentType, string Body);

    private sealed class ManualTranslationClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            return Interlocked.Read(ref ticks);
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        }

        public void Advance(TimeSpan duration)
        {
            Interlocked.Add(ref ticks, duration.Ticks);
        }
    }

    private sealed class TranslationStub : IAsyncDisposable
    {
        private readonly WebApplication application;
        private readonly TaskCompletionSource firstRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource firstResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int requestNumber;

        private TranslationStub(WebApplication application)
        {
            this.application = application;
        }

        public Uri Address { get; private set; } = null!;
        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();
        public Task FirstRequest => firstRequest.Task;
        public Task FirstResponse => firstResponse.Task;

        public static async Task<TranslationStub> StartAsync(Func<HttpContext, int, Task> respond)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var stub = new TranslationStub(app);

            // 모든 경로를 관측하므로 adapter의 잘못된 URL도 성공 응답에 가려지지 않는다.
            app.Run(async context =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                int number = Interlocked.Increment(ref stub.requestNumber);
                stub.Requests.Enqueue(new(context.Request.Method, context.Request.Path,
                    context.Request.Headers["X-API-KEY"].ToString(), context.Request.ContentType ?? "", body));
                stub.firstRequest.TrySetResult();

                try
                {
                    await respond(context, number);
                    await context.Response.CompleteAsync();
                    stub.firstResponse.TrySetResult();
                }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
                {
                    // timeout/caller 취소로 끊긴 이 fixture의 요청만 종료한다.
                }
            });

            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            stub.Address = new Uri(address);
            return stub;
        }

        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await application.StopAsync(deadline.Token);
            await application.DisposeAsync();
        }
    }
}
