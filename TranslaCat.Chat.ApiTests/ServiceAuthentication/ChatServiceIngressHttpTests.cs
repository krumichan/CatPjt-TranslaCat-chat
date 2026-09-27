using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Api.Authentication;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Realtime;
using TranslaCat.Chat.Api.ServiceAuthentication;

namespace TranslaCat.Chat.ApiTests.ServiceAuthentication;

public sealed class ChatServiceIngressHttpTests
{
    [Theory]
    [InlineData("valid", 200)]
    [InlineData("missing-service", 401)]
    [InlineData("wrong-key", 401)]
    [InlineData("wrong-environment", 401)]
    [InlineData("wrong-scope", 401)]
    [InlineData("different-user", 401)]
    [InlineData("no-user", 401)]
    [InlineData("invalid-user", 401)]
    [InlineData("disabled-account", 401)]
    [InlineData("duplicate-header", 401)]
    [InlineData("forged-user-headers", 401)]
    public async Task Actual_HTTP_requires_both_service_and_current_user_identity(string scenario, int status)
    {
        // 준비: 운영 JWT handler와 서비스 middleware를 사용하고 현재 계정 조회만 합성 구현한다.
        await using var host = await IngressHost.StartAsync("Development", true, scenario != "disabled-account");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/chat/service-auth-probe");
        var token = host.ServiceToken(
            scenario == "wrong-environment" ? "Production" : "Development",
            scenario == "wrong-scope" ? "chat:realtime" : "chat:http",
            scenario == "different-user" ? "74" : "73", scenario == "wrong-key");
        if (scenario != "missing-service")
        {
            request.Headers.Add(ChatServiceIngressGuard.HeaderName, "Bearer " + token);
        }

        if (scenario == "duplicate-header")
        {
            request.Headers.Add(ChatServiceIngressGuard.HeaderName, "Bearer " + token);
        }

        if (scenario is not ("no-user" or "forged-user-headers"))
        {
            request.Headers.Authorization = new("Bearer", host.UserToken(scenario == "invalid-user"));
        }
        request.Headers.Add("X-User-Id", "73");
        request.Headers.Add("X-Role", "ROLE_ADMIN");

        // 실행
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // 검증: 거부된 요청은 업무 endpoint까지 도달하지 않는다. 비밀을 응답으로 되돌리지 않는다.
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(status == 200 ? 1 : 0, host.Calls);
        Assert.DoesNotContain(token, body);
        Assert.DoesNotContain(Convert.ToBase64String(host.ServiceKey), body);
    }

    [Theory]
    [InlineData("Development", false, 401)]
    [InlineData("Production", false, 401)]
    [InlineData("Staging", false, 401)]
    [InlineData("Testing", false, 200)]
    [InlineData("Testing", true, 401)]
    public async Task Missing_service_settings_fail_closed_except_explicit_Testing_fixture(string environment, bool enabled, int status)
    {
        // 준비
        await using var host = await IngressHost.StartAsync(environment, enabled);
        host.Client.DefaultRequestHeaders.Authorization = new("Bearer", host.UserToken());

        // 실행
        using var response = await host.Client.GetAsync("/api/v1/users/me/chat-language-settings/");
        using var health = await host.Client.GetAsync("/api/health");
        using var readiness = await host.Client.GetAsync("/api/ready");

        // 검증: trailing slash도 업무 보호 대상이며 health/readiness는 서비스 token을 요구하지 않는다.
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
    }

    [Theory]
    [InlineData("chat:http", "ROLE_ADMIN", 401)]
    [InlineData("chat:admin", "ROLE_USER", 403)]
    [InlineData("chat:admin", "ROLE_ADMIN", 200)]
    public async Task Admin_scope_never_replaces_current_user_admin_role(string scope, string role, int status)
    {
        // 준비
        await using var host = await IngressHost.StartAsync("Development", true, role: role);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/chat/service-auth-probe");
        request.Headers.Authorization = new("Bearer", host.UserToken());
        request.Headers.Add(ChatServiceIngressGuard.HeaderName, "Bearer " + host.ServiceToken(scope: scope));

        // 실행
        using var response = await host.Client.SendAsync(request);

        // 검증
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Equal(status == 200 ? 1 : 0, host.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("http-scope")]
    public async Task Actual_WebSocket_handshake_requires_realtime_service_credential(string failure)
    {
        // 준비
        await using var host = await IngressHost.StartAsync("Development", true);
        using var socket = new ClientWebSocket();
        if (failure != "missing")
        {
            socket.Options.SetRequestHeader(ChatServiceIngressGuard.HeaderName, "Bearer " + host.ServiceToken());
        }

        // 실행 / 검증: STOMP CONNECT 이전 HTTP upgrade 경계에서 거부한다.
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(host.WebSocketUri, CancellationToken.None));
        Assert.NotEqual(WebSocketState.Open, socket.State);
    }

    [Theory]
    [InlineData("73", true)]
    [InlineData("74", false)]
    public async Task Actual_STOMP_CONNECT_identity_must_match_verified_handshake_subject(string serviceUser, bool accepted)
    {
        // 준비: 기존 실제 CHAT STOMP endpoint를 사용한다. service key는 서버 handshake에만 놓는다.
        await using var host = await IngressHost.StartAsync("Development", true);
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(ChatServiceIngressGuard.HeaderName,
            "Bearer " + host.ServiceToken(scope: "chat:realtime", subject: serviceUser));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(host.WebSocketUri, deadline.Token);
        var connect = Encoding.UTF8.GetBytes("CONNECT\naccept-version:1.2\nAuthorization:Bearer " + host.UserToken() + "\n\n\0");

        // 실행
        await socket.SendAsync(connect, WebSocketMessageType.Text, true, deadline.Token);
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, deadline.Token);
        var frame = Encoding.UTF8.GetString(buffer, 0, result.Count);

        // 검증: 서비스 토큰은 정상이어도 다른 사용자의 CONNECT에는 CONNECTED를 주지 않는다.
        Assert.StartsWith(accepted ? "CONNECTED\n" : "ERROR\n", frame);
        Assert.DoesNotContain(host.UserToken(), frame);
        socket.Abort();
    }

    private sealed class IngressHost(WebApplication application, HttpClient client, byte[] serviceKey, byte[] userKey) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public byte[] ServiceKey { get; } = serviceKey;
        public int Calls
        {
            get; private set;
        }
        public Uri WebSocketUri => new UriBuilder(Client.BaseAddress!) { Scheme = "ws", Path = "/ws/chat" }.Uri;

        public string ServiceToken(string environment = "Development", string scope = "chat:http", string subject = "73", bool wrongKey = false)
        {
            return ChatServiceJwtTests.Sign(wrongKey ? RandomNumberGenerator.GetBytes(32) : ServiceKey,
                        ChatServiceJwtTests.Claims(environment, scope, subject));
        }

        public string UserToken(bool wrongKey = false)
        {
            return ChatServiceJwtTests.Sign(wrongKey ? RandomNumberGenerator.GetBytes(32) : userKey, new()
            {
                ["sub"] = "synthetic@example.invalid",
                ["id"] = 73L,
                ["iat"] = ChatServiceJwtTests.Now.ToUnixTimeSeconds(),
                ["exp"] = ChatServiceJwtTests.Now.AddHours(1).ToUnixTimeSeconds()
            });
        }

        public static async Task<IngressHost> StartAsync(string environment, bool enabled, bool accountEnabled = true, string role = "ROLE_USER")
        {
            var serviceKey = RandomNumberGenerator.GetBytes(32);
            var userKey = RandomNumberGenerator.GetBytes(32);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:Authentication:Enabled"] = "true",
                ["Chat:Authentication:Base64SigningKey"] = Convert.ToBase64String(userKey),
                ["Chat:ServiceAuthentication:Ingress:Enabled"] = enabled.ToString(),
                ["Chat:ServiceAuthentication:Ingress:Issuer"] = "translacat-be",
                ["Chat:ServiceAuthentication:Ingress:Audience"] = "translacat-chat",
                ["Chat:ServiceAuthentication:Ingress:Service"] = "translacat-be",
                ["Chat:ServiceAuthentication:Ingress:Base64SigningKey"] = Convert.ToBase64String(serviceKey)
            });
            builder.Services.AddChatReadHttp();
            builder.Services.AddChatJwtAuthentication(builder.Configuration);
            builder.Services.AddChatServiceAuthentication(builder.Configuration);
            builder.Services.AddChatRealtime();
            builder.Services.AddSingleton<TimeProvider>(new ServiceAuthTimeProvider());
            builder.Services.AddSingleton<IChatIdentityResolver>(new SyntheticCurrentAccount(accountEnabled, role));

            var app = builder.Build();
            app.UseWebSockets();
            app.UseRouting();
            app.UseAuthentication();
            app.UseChatServiceAuthentication();
            app.UseAuthorization();
            IngressHost? host = null;
            // 인증 집중 fixture의 endpoint만 합성이다. 실제 room/DB 동등성 검증으로 보고하지 않는다.
            foreach (var path in new[] { "/api/v1/chat/service-auth-probe", "/api/v1/admin/chat/service-auth-probe", "/api/v1/users/me/chat-language-settings" })
            {
                app.MapGet(path, () => { host!.Calls++; return new { accepted = true }; });
            }
            app.MapGet("/api/health", () => "ok");
            app.MapGet("/api/ready", () => "ready");
            app.MapChatRealtime();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            host = new(app, new HttpClient { BaseAddress = new Uri(address) }, serviceKey, userKey);
            return host;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }

    private sealed class SyntheticCurrentAccount(bool enabled, string role) : IChatIdentityResolver
    {
        public Task<ChatResolvedIdentity?> ResolveAsync(ChatIdentityLookup lookup, CancellationToken cancellationToken)
        {
            return Task.FromResult<ChatResolvedIdentity?>(new(73, "synthetic@example.invalid", role, enabled));
        }
    }
}

internal sealed class ServiceAuthTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow()
    {
        return ChatServiceJwtTests.Now;
    }
}
