using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TranslaCat.Chat.Api.Ai;
using TranslaCat.Chat.Api.Configuration;
using TranslaCat.Chat.Api.Translation;
using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.Translation;

namespace TranslaCat.Chat.ApiTests.Configuration;

public sealed class ChatOutboundConfigurationTests
{
    private const string SyntheticKey = "synthetic-outbound-key-not-a-provider-credential";
    private const string ProductionShapeKey = "mUJ3OvSnvtzmvN20zGhFHRbltoCsmGIWbaDrhIkC2uE=";

    public static IEnumerable<object[]> EndpointCases()
    {
        foreach (string section in new[] { "Chat:Ai", "Chat:Translation" })
        {
            yield return [section, "Development", "http://127.0.0.1:1", true];
            yield return [section, "Development", "http://localhost:1", true];
            yield return [section, "Development", "http://[::1]:1", true];
            yield return [section, "Development", "http://ai.example.invalid", false];
            yield return [section, "Development", "https://ai.example.invalid", true];
            yield return [section, "Production", "https://ai.example.invalid", true];
            yield return [section, "Production", "http://127.0.0.1:1", false];
            yield return [section, "Staging", "http://127.0.0.1:1", false];
            yield return [section, "Staging", "https://ai.example.invalid", true];
            yield return [section, "Production", "https://user:secret@ai.example.invalid", false];
            yield return [section, "Production", "https://ai.example.invalid/?key=private", false];
            yield return [section, "Production", "https://ai.example.invalid/#private", false];
        }
    }

    [Theory]
    [MemberData(nameof(EndpointCases))]
    public void Enabled_endpoint_must_match_environment_and_preserve_existing_URL_restrictions(
        string section, string environment, string endpoint, bool valid)
    {
        // 준비 — DNS나 TLS 연결을 시도하지 않는 순수 설정 검증이다.
        var configuration = Configuration(section, endpoint, ProductionShapeKey);

        // 실행
        var failure = Record.Exception(() => ChatOutboundConfiguration.Validate(configuration, environment));

        // 검증
        if (valid)
        {
            Assert.Null(failure);
        }
        else
        {
            AssertSanitized(failure, section, endpoint, ProductionShapeKey);
        }
    }

    [Theory]
    [InlineData("Chat:Ai")]
    [InlineData("Chat:Translation")]
    public void Disabled_default_does_not_require_endpoint_or_credentials(string section)
    {
        // 준비
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [$"{section}:Enabled"] = "false" }).Build();

        // 실행
        var failure = Record.Exception(() => ChatOutboundConfiguration.Validate(configuration, "Production"));

        // 검증
        Assert.Null(failure);
    }

    [Theory]
    [InlineData("Chat:Ai", null, "opaque-key")]
    [InlineData("Chat:Ai", "https://ai.example.invalid", null)]
    [InlineData("Chat:Ai", "https://ai.example.invalid", " ")]
    [InlineData("Chat:Translation", null, "opaque-key")]
    [InlineData("Chat:Translation", "https://ai.example.invalid", null)]
    [InlineData("Chat:Translation", "https://ai.example.invalid", " ")]
    public void Explicit_enable_requires_both_endpoint_and_key(string section, string? endpoint, string? key)
    {
        // 준비
        var configuration = Configuration(section, endpoint, key);

        // 실행
        var failure = Record.Exception(() => ChatOutboundConfiguration.Validate(configuration, "Production"));

        // 검증
        AssertSanitized(failure, section);
    }

    [Theory]
    [InlineData("CHANGE_ME")]
    [InlineData("replace-with-real-key")]
    [InlineData("<CHAT_AI_KEY>")]
    [InlineData("${CHAT_AI_KEY}")]
    [InlineData("%CHAT_AI_KEY%")]
    [InlineData("synthetic-example")]
    [InlineData("test-only-key")]
    [InlineData("development-key")]
    [InlineData("your_api_key")]
    public void Production_rejects_obvious_placeholders_for_both_features(string key)
    {
        foreach (string section in new[] { "Chat:Ai", "Chat:Translation" })
        {
            // 준비
            var configuration = Configuration(section, "https://ai.example.invalid", key);

            // 실행
            var failure = Record.Exception(() => ChatOutboundConfiguration.Validate(configuration, "Production"));

            // 검증
            AssertSanitized(failure, section, key);
        }
    }

    [Theory]
    [InlineData("Chat:Ai", "MaximumAttempts", "4")]
    [InlineData("Chat:Ai", "BatchTimeZone", "synthetic-invalid-time-zone")]
    [InlineData("Chat:Ai", "AttemptTimeout", "private-malformed-timeout")]
    [InlineData("Chat:Ai", "ApiKey", "private\r\nkey")]
    [InlineData("Chat:Translation", "MaximumAttempts", "0")]
    [InlineData("Chat:Translation", "LeaseDuration", "00:00:01")]
    [InlineData("Chat:Translation", "AttemptTimeout", "private-malformed-timeout")]
    [InlineData("Chat:Translation", "ApiKey", "private\r\nkey")]
    public void Existing_option_validation_and_binding_failures_have_sanitized_diagnostics(
        string section, string option, string value)
    {
        // 준비
        var configuration = Configuration(section, "https://ai.example.invalid", ProductionShapeKey);
        configuration[$"{section}:{option}"] = value;

        // 실행
        var failure = Record.Exception(() => ChatOutboundConfiguration.Validate(configuration, "Production"));

        // 검증
        AssertSanitized(failure, section, value, ProductionShapeKey);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Production_DI_redacts_headers_while_sending_the_original_opaque_key(bool ai)
    {
        // 준비 — 실제 production DI의 handler를 사용하되 주소는 이 시험이 소유한 loopback뿐이다.
        await using var server = await OutboundStub.StartAsync(async context =>
        {
            await context.Response.WriteAsync(ai
                ? "{\"output\":{\"shouldRespond\":false,\"reply\":null,\"languageCode\":null},\"providerCalls\":1}"
                : "{\"output\":\"synthetic\",\"providerCalls\":1}");
        });
        var logs = new CapturedLogs();
        using var services = Services(ai, server.Address, logs);

        // 실행
        await CallAsync(services, ai);

        // 검증 — options와 실제 trace 로그를 확인한다. TLS handshake/provider 인증은 이 시험 범위 밖이다.
        Assert.True(server.ReceivedOriginalKey);
        Assert.Equal(1, server.RequestCount);
        string name = ai ? nameof(IChatAiReplyClient) : nameof(IChatTranslationClient);
        var options = services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(name);
        Assert.True(options.ShouldRedactHeaderValue("x-api-key"));
        Assert.True(options.ShouldRedactHeaderValue("Authorization"));
        Assert.Contains(logs.Messages, value => value.Contains("X-API-KEY", StringComparison.OrdinalIgnoreCase));
        Assert.False(logs.Messages.Any(value => value.Contains(SyntheticKey, StringComparison.Ordinal)),
            "HTTP trace logging must not expose the synthetic credential.");
    }

    [Theory]
    [InlineData(true, 302)]
    [InlineData(true, 307)]
    [InlineData(true, 308)]
    [InlineData(false, 302)]
    [InlineData(false, 307)]
    [InlineData(false, 308)]
    public async Task Production_DI_does_not_follow_redirects_or_forward_credentials(bool ai, int status)
    {
        // 준비 — redirect 수신자도 별도로 소유한 loopback이며 외부 서버는 사용하지 않는다.
        await using var destination = await OutboundStub.StartAsync(_ => Task.CompletedTask);
        await using var origin = await OutboundStub.StartAsync(context =>
        {
            context.Response.StatusCode = status;
            context.Response.Headers.Location = destination.Address.ToString();
            return Task.CompletedTask;
        });
        using var services = Services(ai, origin.Address, new CapturedLogs());

        // 실행
        var failure = await Record.ExceptionAsync(() => CallAsync(services, ai));

        // 검증 — 업무 retry 설정은 이 시험에서 1회로 고정하고 redirect 동작만 확인한다.
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(1, origin.RequestCount);
        Assert.True(origin.ReceivedOriginalKey);
        Assert.Equal(0, destination.RequestCount);
        Assert.False(destination.ReceivedOriginalKey);
        Assert.DoesNotContain(SyntheticKey, failure.ToString());
    }

    private static IConfigurationRoot Configuration(string section, string? endpoint, string? key)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{section}:Enabled"] = "true",
            [$"{section}:AiBaseUri"] = endpoint,
            [$"{section}:ApiKey"] = key
        }).Build();
    }

    private static void AssertSanitized(Exception? failure, string section, params string[] forbidden)
    {
        var invalid = Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains(section, invalid.Message);
        Assert.Null(invalid.InnerException);
        foreach (string value in forbidden)
        {
            Assert.DoesNotContain(value, invalid.Message);

            // 짧은 숫자 설정은 stack trace의 줄 번호와 구분한다. 비밀 marker는 전체 진단을 검사한다.
            if (value.Length > 2)
            {
                Assert.DoesNotContain(value, invalid.ToString());
            }
        }
    }

    private static ServiceProvider Services(bool ai, Uri endpoint, CapturedLogs logs)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton(TimeProvider.System);
        if (ai)
        {
            services.AddChatAi(new ChatAiOptions
            {
                Enabled = true,
                AiBaseUri = endpoint,
                ApiKey = SyntheticKey,
                MaximumAttempts = 1,
                AttemptTimeout = TimeSpan.FromSeconds(5),
                ConnectTimeout = TimeSpan.FromSeconds(1),
                RetryWait = TimeSpan.Zero
            });
        }
        else
        {
            services.AddChatTranslation(new ChatTranslationOptions
            {
                Enabled = true,
                AiBaseUri = endpoint,
                ApiKey = SyntheticKey,
                MaximumAttempts = 1,
                AttemptTimeout = TimeSpan.FromSeconds(5),
                ConnectTimeout = TimeSpan.FromSeconds(1),
                RetryWait = TimeSpan.Zero
            });
        }

        return services.BuildServiceProvider();
    }

    private static async Task CallAsync(IServiceProvider services, bool ai)
    {
        if (ai)
        {
            var request = new ChatAiReplyRequest("synthetic", "MENTION", new(1, "GROUP", null, null),
                new(2, "synthetic", null, null, "en"),
                new(3, "member", "Member", "synthetic", new DateTime(2026, 9, 27)),
                [], 30, 12000, 800);
            await services.GetRequiredService<IChatAiReplyClient>().GenerateAsync(request, default);
        }
        else
        {
            await services.GetRequiredService<IChatTranslationClient>().TranslateAsync("synthetic", "en", default);
        }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName)
        {
            return new Logger(Messages);
        }

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                            Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
            }
        }
    }

    private sealed class OutboundStub(WebApplication application) : IAsyncDisposable
    {
        private int requests;
        public Uri Address { get; private set; } = null!;
        public int RequestCount => Volatile.Read(ref requests);
        public bool ReceivedOriginalKey
        {
            get; private set;
        }

        public static async Task<OutboundStub> StartAsync(RequestDelegate respond)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var stub = new OutboundStub(app);
            app.Run(async context =>
            {
                Interlocked.Increment(ref stub.requests);
                stub.ReceivedOriginalKey = context.Request.Headers["X-API-KEY"] == SyntheticKey;
                await respond(context);
            });
            await app.StartAsync();
            stub.Address = new Uri(app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single());
            return stub;
        }

        public async ValueTask DisposeAsync()
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await application.StopAsync(cancellation.Token);
            await application.DisposeAsync();
        }
    }
}
