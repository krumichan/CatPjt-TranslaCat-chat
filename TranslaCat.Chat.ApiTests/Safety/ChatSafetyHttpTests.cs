using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Api.Safety;
using TranslaCat.Chat.Api.ServiceAuthentication;
using TranslaCat.Chat.Application.Safety;

namespace TranslaCat.Chat.ApiTests.Safety;

public sealed class ChatSafetyHttpTests
{
    [Fact]
    public void Disabled_feature_does_not_register_database_policy_or_cleanup_workers()
    {
        // 준비: 운영 설정/DB/비밀을 읽지 않는 빈 설정으로 기본 비활성 상태를 검사한다.
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        // 실행
        services.AddChatSafety(configuration);
        // 검증: 기존 core mutation에 전달되는 optional policy는 기본적으로 존재하지 않는다.
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IChatContentPolicy));
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == typeof(ChatAccountDeletionWorker));
        Assert.DoesNotContain(services, descriptor => descriptor.ImplementationType == typeof(ChatReportEvidenceWorker));
    }

    [Fact]
    public void Complete_write_coverage_registers_cleanup_only_with_explicit_enabled_configuration()
    {
        // 준비: source coverage는 완성되어도 기본 false 설정을 켠 배포만 삭제 port를 등록한다.
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Chat:Safety:Enabled"] = "true", ["Chat:Safety:AccountDeletionEnabled"] = "true"
        }).Build();
        // 실행
        services.AddChatSafety(configuration);
        // 검증: 실제 mutation·journal 검증을 마친 source를 환경 flag가 명시적으로 선택한다.
        Assert.True(ChatAccountDeletionReleaseGate.NormalWriteCoverageComplete);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IChatAccountDeletionStore));
        Assert.Contains(services, descriptor => descriptor.ImplementationType == typeof(ChatAccountDeletionWorker));
    }
    private const string ReportBody = "{\"targetType\":\"MESSAGE\",\"messageId\":51,\"reason\":\"SPAM\",\"clientRequestId\":\"d6e76159-879b-4e6e-a210-40d176d81cb1\"}";
    private const string DeleteBody = "{\"operationId\":\"d6e76159-879b-4e6e-a210-40d176d81cb1\",\"requestedAtUtc\":\"2026-10-23T00:00:00Z\",\"auditIdentity\":\"synthetic@example.invalid\"}";

    [Theory]
    [InlineData("valid", 200)]
    [InlineData("no-user", 401)]
    [InlineData("missing-service", 401)]
    [InlineData("wrong-scope", 401)]
    [InlineData("wrong-user", 401)]
    public async Task Report_creation_requires_current_user_and_matching_service_identity(string scenario, int expected)
    {
        // 준비: 실제 Kestrel/운영 service middleware와 controller를 사용하고 저장소만 합성으로 격리한다.
        await using var host = await Host.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/rooms/41/reports") { Content = Json(ReportBody) };
        if (scenario != "no-user") request.Headers.Add("X-Safety-Test-User", "73");
        if (scenario != "missing-service") request.Headers.Add(ChatServiceIngressGuard.HeaderName, "Bearer " + host.Token(
            "chat-ingress", scenario == "wrong-scope" ? "chat:realtime" : "chat:http", scenario == "wrong-user" ? 74 : 73));

        // 실행
        using var response = await host.Client.SendAsync(request);

        // 검증: 신고자 ID는 body로 받지 않고 인증 ID만 store에 전달한다.
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.Equal(expected == 200 ? 1 : 0, host.Store.CreateCalls);
        if (expected == 200)
        {
            Assert.Equal(73, host.Store.Reporter);
            string body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("reporterUserId", body);
            Assert.DoesNotContain("targetUserId", body);
            Assert.Contains("evidenceExpiresAt", body);
        }
    }

    [Theory]
    [InlineData("chat:admin", "ROLE_ADMIN", 200)]
    [InlineData("chat:http", "ROLE_ADMIN", 401)]
    [InlineData("chat:admin", "ROLE_USER", 403)]
    public async Task Administrator_report_access_needs_both_admin_scope_and_role(string scope, string role, int expected)
    {
        // 준비
        await using var host = await Host.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/chat/reports");
        request.Headers.Add("X-Safety-Test-User", "73");
        request.Headers.Add("X-Safety-Test-Role", role);
        request.Headers.Add(ChatServiceIngressGuard.HeaderName, "Bearer " + host.Token("chat-ingress", scope));

        // 실행 / 검증
        using var response = await host.Client.SendAsync(request);
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.Equal(expected == 200 ? 1 : 0, host.Store.ListCalls);
    }

    [Theory]
    [InlineData("{\"targetType\":\"MESSAGE\",\"targetType\":\"MEMBER\",\"messageId\":51,\"reason\":\"SPAM\",\"clientRequestId\":\"d6e76159-879b-4e6e-a210-40d176d81cb1\"}")]
    [InlineData("{\"targetType\":\"MESSAGE\",\"messageId\":51,\"reason\":\"SPAM\",\"clientRequestId\":\"d6e76159-879b-4e6e-a210-40d176d81cb1\",\"reporterUserId\":99}")]
    [InlineData("{")]
    public async Task Ambiguous_or_forged_JSON_never_calls_report_store(string json)
    {
        // 준비
        await using var host = await Host.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/rooms/41/reports") { Content = Json(json) };
        request.Headers.Add("X-Safety-Test-User", "73");
        request.Headers.Add(ChatServiceIngressGuard.HeaderName, "Bearer " + host.Token("chat-ingress", "chat:http"));

        // 실행 / 검증
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Store.CreateCalls);
    }

    [Theory]
    [InlineData("valid", 200)]
    [InlineData("missing", 401)]
    [InlineData("normal-ingress", 401)]
    [InlineData("wrong-target", 401)]
    [InlineData("wrong-environment", 401)]
    public async Task Account_cleanup_requires_target_bound_token_and_complete_write_coverage(string scenario, int expected)
    {
        // 준비: 인증된 실제 사용자 계정이나 외부 서버는 사용하지 않는다.
        await using var host = await Host.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/chat/accounts/73/deletion") { Content = Json(DeleteBody) };
        if (scenario != "missing") request.Headers.Authorization = new("Bearer", host.Token(
            scenario == "normal-ingress" ? "chat-ingress" : "chat-account-delete",
            scenario == "normal-ingress" ? "chat:http" : "chat:account-delete",
            scenario == "wrong-target" ? 74 : 73, scenario == "wrong-environment" ? "Production" : "Testing"));

        // 실행 / 검증
        using var response = await host.Client.SendAsync(request);
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        Assert.Equal(expected == 200 ? 1 : 0, host.Deletion.Calls);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("synthetic@example.invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Anonymous_malformed_cleanup_is_rejected_before_body_binding_and_disabled_feature_never_mutates()
    {
        // 준비
        await using var host = await Host.StartAsync(enabled: false);
        using var anonymous = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/chat/accounts/73/deletion") { Content = Json("{") };
        using var authorized = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/chat/accounts/73/deletion") { Content = Json(DeleteBody) };
        authorized.Headers.Authorization = new("Bearer", host.Token("chat-account-delete", "chat:account-delete"));

        // 실행 / 검증
        using var rejected = await host.Client.SendAsync(anonymous);
        using var disabled = await host.Client.SendAsync(authorized);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        Assert.Equal(0, host.Deletion.Calls);
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");
    private sealed class Host(WebApplication application, HttpClient client, byte[] key, Store store, Deletion deletion) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public Store Store { get; } = store;
        public Deletion Deletion { get; } = deletion;
        public string Token(string use, string scope, long userId = 73, string environment = "Testing") => ChatServiceJwt.Issue(new()
            { Issuer = "translacat-be", Audience = "translacat-chat", Service = "translacat-be", Base64SigningKey = Convert.ToBase64String(key) },
            environment, use, scope, userId, DateTimeOffset.UtcNow);
        public static async Task<Host> StartAsync(bool enabled = true)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            byte[] key = RandomNumberGenerator.GetBytes(32);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ChatServiceIngressOptions.SectionName + ":Enabled"] = "true",
                [ChatServiceIngressOptions.SectionName + ":Issuer"] = "translacat-be",
                [ChatServiceIngressOptions.SectionName + ":Audience"] = "translacat-chat",
                [ChatServiceIngressOptions.SectionName + ":Service"] = "translacat-be",
                [ChatServiceIngressOptions.SectionName + ":Base64SigningKey"] = Convert.ToBase64String(key)
            });
            builder.Services.AddChatReadHttp();
            builder.Services.AddChatServiceAuthentication(builder.Configuration);
            builder.Services.AddSingleton(new ChatSafetyOptions { Enabled = enabled, AccountDeletionEnabled = enabled });
            builder.Services.AddSingleton(new ChatReadTimestampFormatter(TimeZoneInfo.Utc));
            var store = new Store();
            var deletion = new Deletion();
            builder.Services.AddSingleton<IChatReportStore>(store);
            builder.Services.AddSingleton<IChatAccountDeletionStore>(deletion);
            builder.Services.AddAuthentication("SafetyTest").AddScheme<AuthenticationSchemeOptions, TestAuthentication>("SafetyTest", _ => { });
            var app = builder.Build();
            app.UseChatReadHttp();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, new HttpClient { BaseAddress = new Uri(address) }, key, store, deletion);
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await application.StopAsync(); await application.DisposeAsync(); }
    }
    private sealed class Store : IChatReportStore
    {
        public int CreateCalls { get; private set; }
        public int ListCalls { get; private set; }
        public long Reporter { get; private set; }
        private static ChatReportReceipt Receipt => new(5, 41, "MESSAGE", "SPAM", "PENDING",
            DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified), DateTime.SpecifyKind(DateTime.UtcNow.AddDays(90), DateTimeKind.Unspecified));
        public Task<ChatReportReceipt> CreateAsync(long userId, long roomId, ChatReportCreateRequest request, DateTime now, CancellationToken token)
        { CreateCalls++; Reporter = userId; return Task.FromResult(Receipt); }
        public Task<ChatReportPage> ListAsync(string? status, long? cursor, int size, DateTime now, CancellationToken token)
        { ListCalls++; return Task.FromResult(new ChatReportPage([Receipt], null, false)); }
        public Task<ChatReportDetail> GetAsync(long id, DateTime now, CancellationToken token) => Task.FromResult(new ChatReportDetail(Receipt, null, "합성 비공개", null, null, now));
        public Task<ChatReportDetail> UpdateAsync(long reviewer, long id, ChatReportStatusRequest request, DateTime now, CancellationToken token) => GetAsync(id, now, token);
        public Task PurgeExpiredEvidenceAsync(DateTime now, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class Deletion : IChatAccountDeletionStore
    {
        public int Calls { get; private set; }
        public Task<ChatAccountDeletionResult> DeleteAsync(long id, ChatAccountDeletionRequest request, CancellationToken token)
        { Calls++; return Task.FromResult(new ChatAccountDeletionResult(request.OperationId!, id, "COMPLETED", DateTimeOffset.UtcNow)); }
    }
    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Safety-Test-User", out var id)) return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Role, Request.Headers["X-Safety-Test-Role"].ToString())], "SafetyTest");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "SafetyTest")));
        }
    }
}
