using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Api.Ai;
using TranslaCat.Chat.Api.Translation;
using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.Translation;
using TranslaCat.Chat.Infrastructure.Ai;

// 준비 — 이 harness가 만든 loopback Python 프로세스와 합성 자격증명만 사용한다.
if (Environment.GetEnvironmentVariable("CHAT_AUTH_CONTRACT_TEST") != "true")
{
    throw new InvalidOperationException("Explicit local contract test is required.");
}

var origin = new Uri(Environment.GetEnvironmentVariable("CHAT_AUTH_CONTRACT_ORIGIN")!);
var key = Environment.GetEnvironmentVariable("CHAT_AUTH_CONTRACT_KEY")!;
var legacyKey = Environment.GetEnvironmentVariable("CHAT_AUTH_CONTRACT_LEGACY_KEY")!;
var mode = Environment.GetEnvironmentVariable("CHAT_AUTH_CONTRACT_MODE")!;
if (origin.Scheme != "http" || origin.Host != "127.0.0.1" || mode is not ("dual" or "dedicated"))
{
    throw new InvalidOperationException("Only the owned loopback fixture is allowed.");
}

var request = new ChatAiReplyRequest("synthetic-request", "MENTION", new(1, "GROUP", null, null),
    new(2, "synthetic", null, null, "en"),
    new(3, "member-1", "synthetic", "synthetic", new DateTime(2026, 9, 27, 1, 0, 0)), [], 30, 12000, 800);
string executionJson = JsonSerializer.Serialize(
    ChatTranslationExecutionPolicy.Build("synthetic", "en"),
    new JsonSerializerOptions(JsonSerializerDefaults.Web));
int passed = 0;

// 실행 / 검증 — CHAT 운영 typed client가 실제 FastAPI와 OpenAIService의 SDK transport 대역까지 호출한다.
using (var services = Clients(key))
{
    var translation = await services.GetRequiredService<IChatTranslationClient>().TranslateAsync("synthetic", "en", default);
    Require(translation == "synthetic translation");
    var reply = await services.GetRequiredService<IChatAiReplyClient>().GenerateAsync(request, default);
    Require(reply is { RequestId: "synthetic-request", ShouldRespond: false, Reply: null, LanguageCode: null });
    passed += 2;
}

// 실제 Python 기술 오류 경로가 긴 Retry-After를 보존하며 추가 Provider 호출을 만들지 않는다.
foreach (int seconds in new[] { 600, 86_400 })
{
    using var services = Clients(key);
    await ExpectDeferred(() => services.GetRequiredService<IChatTranslationClient>()
        .TranslateAsync($"synthetic-retry-after-{seconds}", "en", default), seconds);
    var throttled = request with
    {
        TriggerMessage = request.TriggerMessage! with
        {
            Content = $"synthetic-retry-after-{seconds}"
        }
    };
    await ExpectDeferred(() => services.GetRequiredService<IChatAiReplyClient>()
        .GenerateAsync(throttled, default), seconds);
    passed += 2;
}

// 다른 자격증명으로는 typed client가 Provider에 도달할 수 없다.
using (var services = Clients("synthetic-wrong-key"))
{
    await ExpectFailure(() => services.GetRequiredService<IChatTranslationClient>().TranslateAsync("synthetic", "en", default));
    await ExpectFailure(() => services.GetRequiredService<IChatAiReplyClient>().GenerateAsync(request, default));
    passed += 2;
}

using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
{
    Timeout = TimeSpan.FromSeconds(5)
};

// 전용 키는 범용 모델 실행 POST에만 유효하고, LL의 기존 공통 키도 유지된다.
foreach (string? rejected in new string?[] { null, "", "synthetic-wrong-key", "synthetic-opposite-direction", "synthetic-other-environment", "synthetic-revoked-key" })
{
    using var response = await Send(HttpMethod.Post, "/internal/v1/model/execute", rejected);
    Require(response.StatusCode == HttpStatusCode.Unauthorized);
    passed++;
}

using (var legacy = await Send(HttpMethod.Post, "/internal/v1/model/execute", legacyKey))
{
    Require(legacy.StatusCode == HttpStatusCode.OK);
    passed++;
}

foreach (var (method, path) in new[]
{
    (HttpMethod.Get, "/internal/v1/model/execute"),
    (HttpMethod.Post, "/internal/v1/model/execute/"),
    (HttpMethod.Post, "/internal/v1/speech/transcribe"),
    (HttpMethod.Post, "/api/v1/voice/translate"),
    (HttpMethod.Get, "/docs")
})
{
    using var response = await Send(method, path, key);
    Require(response.StatusCode == HttpStatusCode.Unauthorized);
    passed++;
}

// 서비스 대역 호출 수는 성공 3회와 기술 오류 4회만 발생했음을 증명한다.
using var countsResponse = await Send(HttpMethod.Get, "/_synthetic/chat-execution/counts", legacyKey);
Require(countsResponse.StatusCode == HttpStatusCode.OK);
using var counts = JsonDocument.Parse(await countsResponse.Content.ReadAsStringAsync());
Require(counts.RootElement.GetProperty("model").GetInt32() == 7);
Require(counts.RootElement.GetProperty("structured").GetInt32() == 3);
passed++;
Console.WriteLine(JsonSerializer.Serialize(new { probe = "actual-chat-clients-to-python-execution", mode, passed, failed = 0, provider = "synthetic-sdk-transport" }));

ServiceProvider Clients(string credential)
{
    var services = new ServiceCollection();
    services.AddLogging(builder => builder.ClearProviders());
    services.AddSingleton(TimeProvider.System);
    services.AddChatAi(new ChatAiOptions
    {
        Enabled = true, AiBaseUri = origin, ApiKey = credential, MaximumAttempts = 1,
        AttemptTimeout = TimeSpan.FromSeconds(5), ConnectTimeout = TimeSpan.FromSeconds(1), RetryWait = TimeSpan.Zero
    });
    services.AddChatTranslation(new ChatTranslationOptions
    {
        Enabled = true, AiBaseUri = origin, ApiKey = credential, MaximumAttempts = 1,
        ConnectTimeout = TimeSpan.FromSeconds(1), RetryWait = TimeSpan.Zero,
        AttemptTimeout = TimeSpan.FromSeconds(5), LeaseDuration = TimeSpan.FromSeconds(30)
    });
    return services.BuildServiceProvider();
}

async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? credential)
{
    using var message = new HttpRequestMessage(method, new Uri(origin, path));
    if (method == HttpMethod.Post)
    {
        message.Content = new StringContent(executionJson, Encoding.UTF8, "application/json");
    }
    if (credential is not null)
    {
        message.Headers.TryAddWithoutValidation("X-API-KEY", credential);
    }
    return await http.SendAsync(message);
}

static async Task ExpectFailure(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (InvalidOperationException failure) when (failure.Message is "AI Server Chat Reply Error" or "AI Server Chat Translation Error")
    {
        return;
    }
    throw new InvalidOperationException("Wrong credential was not rejected by the typed client.");
}

static async Task ExpectDeferred(Func<Task> action, int seconds)
{
    try
    {
        await action();
    }
    catch (ChatExecutionDeferredException deferred) when (
        deferred.RetryAfter == TimeSpan.FromSeconds(seconds)
        && deferred.InnerException is ChatModelExecutionFailure failure
        && failure.RetryAfterSeconds == seconds)
    {
        return;
    }

    throw new InvalidOperationException("Long Provider retry-after was not preserved.");
}

static void Require(bool result)
{
    if (!result)
    {
        throw new InvalidOperationException("Synthetic Chat AI execution contract assertion failed.");
    }
}
