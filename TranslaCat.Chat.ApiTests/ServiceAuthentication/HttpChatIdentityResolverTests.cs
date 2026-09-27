using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Api.Authentication;
using TranslaCat.Chat.Api.ServiceAuthentication;

namespace TranslaCat.Chat.ApiTests.ServiceAuthentication;

public sealed class HttpChatIdentityResolverTests
{
    [Fact]
    public async Task Actual_HTTP_posts_fixed_contract_with_separate_signed_scoped_credential()
    {
        // 준비: 상대 BE만 로컬 합성 server다. production HTTP client/서명/직렬화/응답 검증은 실제 구현이다.
        await using var host = await IdentityHost.StartAsync("valid");

        // 실행
        var result = await host.Resolver.ResolveAsync(host.Lookup, CancellationToken.None);

        // 검증
        Assert.Equal(new ChatResolvedIdentity(73, "synthetic@example.invalid", "ROLE_USER", true), result);
        Assert.Equal(1, host.Hits);
        Assert.Equal("POST", host.LastMethod);
        Assert.Equal(HttpChatIdentityResolver.IdentityPath, host.LastPath);
        using var body = JsonDocument.Parse(host.LastBody!);
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        Assert.Equal(host.Lookup.Subject, body.RootElement.GetProperty("subject").GetString());
        Assert.Equal(73, body.RootElement.GetProperty("tokenUserId").GetInt64());
        Assert.Null(host.IngressHeader);
        var verified = await ChatServiceJwt.ValidateAsync(host.Token!, host.SigningSettings, "Development",
            "chat-identity", new HashSet<string> { "chat:identity:read" }, ChatServiceJwtTests.Now);
        Assert.NotNull(verified);
        Assert.Equal(73, verified.UserId);
        Assert.Null(await ChatServiceJwt.ValidateAsync(host.Token!, host.SigningSettings, "Development",
            "chat-ingress", ChatServiceJwtTests.AllowedScopes, ChatServiceJwtTests.Now));
    }

    [Theory]
    [InlineData("disabled-account")]
    [InlineData("not-found")]
    public async Task Current_account_state_and_absence_are_preserved(string scenario)
    {
        // 준비
        await using var host = await IdentityHost.StartAsync(scenario);

        // 실행
        var identity = await host.Resolver.ResolveAsync(host.Lookup, CancellationToken.None);

        // 검증
        if (scenario == "not-found")
        {
            Assert.Null(identity);
        }
        else
        {
            Assert.False(Assert.IsType<ChatResolvedIdentity>(identity).CanAuthenticate);
        }
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("wrong-email")]
    [InlineData("wrong-role")]
    [InlineData("missing-state")]
    [InlineData("null-state")]
    [InlineData("string-state")]
    [InlineData("string-id")]
    [InlineData("duplicate-field")]
    [InlineData("extra-field")]
    [InlineData("malformed")]
    [InlineData("oversize")]
    [InlineData("server-error")]
    [InlineData("redirect")]
    public async Task Invalid_or_redirected_upstream_response_fails_closed_without_sensitive_errors(string scenario)
    {
        // 준비
        await using var host = await IdentityHost.StartAsync(scenario);

        // 실행
        var error = await Assert.ThrowsAsync<ChatIdentityUnavailableException>(() => host.Resolver.ResolveAsync(host.Lookup, CancellationToken.None));

        // 검증: 오류 body나 토큰을 예외로 전달하지 않고 redirect 목적지는 호출하지 않는다.
        Assert.Equal("Chat identity service is unavailable.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(1, host.Hits);
        Assert.Equal(0, host.RedirectHits);
        Assert.DoesNotContain(host.Lookup.Subject, error.ToString());
    }

    [Theory]
    [InlineData("Production", "http-loopback")]
    [InlineData("Testing", "http-loopback")]
    [InlineData("Development", "remote-http")]
    [InlineData("Development", "embedded-path")]
    [InlineData("Development", "userinfo")]
    [InlineData("Development", "query")]
    [InlineData("Development", "fragment")]
    [InlineData("Development", "disabled")]
    [InlineData("Development", "missing-key")]
    public async Task Unsafe_origin_or_disabled_configuration_never_sends_identity_or_service_secret(string environment, string scenario)
    {
        // 준비
        await using var host = await IdentityHost.StartAsync("valid", environment, scenario);

        // 실행 / 검증
        await Assert.ThrowsAsync<ChatIdentityUnavailableException>(() => host.Resolver.ResolveAsync(host.Lookup, CancellationToken.None));
        Assert.Equal(0, host.Hits);
        Assert.Equal(scenario != "disabled", host.HasRegisteredIdentityAdapter);
    }

    [Fact]
    public async Task Total_HTTP_timeout_is_unavailable_but_caller_cancellation_is_preserved()
    {
        // 준비
        await using var host = await IdentityHost.StartAsync("delay");

        // 실행 / 검증: timeout은 인증 성공이나 무한 대기가 되지 않는다.
        await Assert.ThrowsAsync<ChatIdentityUnavailableException>(() => host.Resolver.ResolveAsync(host.Lookup, CancellationToken.None));
        Assert.Equal(1, host.Hits);

        // 준비 / 실행 / 검증: 요청 취소는 별도 계약이며 호출자에게 취소로 전달한다.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Resolver.ResolveAsync(host.Lookup, cancellation.Token));
        Assert.Equal(2, host.Hits);
    }

    private sealed class IdentityHost(WebApplication application, ServiceProvider services) : IAsyncDisposable
    {
        public ChatIdentityLookup Lookup { get; } = new("synthetic@example.invalid", 73);
        public IChatIdentityResolver Resolver => services.GetRequiredService<HttpChatIdentityResolver>();
        public bool HasRegisteredIdentityAdapter => services.GetService<IChatIdentityResolver>() is not null;
        public ChatServiceTokenSettings SigningSettings { get; private set; } = null!;
        public int Hits
        {
            get; private set;
        }
        public int RedirectHits
        {
            get; private set;
        }
        public string? LastPath
        {
            get; private set;
        }
        public string? LastMethod
        {
            get; private set;
        }
        public string? LastBody
        {
            get; private set;
        }
        public string? Token
        {
            get; private set;
        }
        public string? IngressHeader
        {
            get; private set;
        }

        public static async Task<IdentityHost> StartAsync(string responseScenario, string environment = "Development", string configurationScenario = "valid")
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            IdentityHost? host = null;
            app.MapPost(HttpChatIdentityResolver.IdentityPath, async context =>
            {
                host!.Hits++;
                host.LastPath = context.Request.Path;
                host.LastMethod = context.Request.Method;
                host.LastBody = await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted);
                host.Token = context.Request.Headers.Authorization.ToString()["Bearer ".Length..];
                host.IngressHeader = context.Request.Headers.TryGetValue(ChatServiceIngressGuard.HeaderName, out var ingress) ? ingress.ToString() : null;
                if (responseScenario == "redirect")
                {
                    context.Response.StatusCode = 302;
                    context.Response.Headers.Location = "/credential-sink";
                    return;
                }
                if (responseScenario == "not-found")
                {
                    context.Response.StatusCode = 404;
                    return;
                }
                if (responseScenario == "server-error")
                {
                    context.Response.StatusCode = 503;
                    return;
                }
                if (responseScenario == "delay")
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
                    return;
                }

                var payload = responseScenario switch
                {
                    "wrong-id" => "{\"userId\":74,\"email\":\"synthetic@example.invalid\",\"role\":\"ROLE_USER\",\"canAuthenticate\":true}",
                    "wrong-email" => "{\"userId\":73,\"email\":\"other@example.invalid\",\"role\":\"ROLE_USER\",\"canAuthenticate\":true}",
                    "wrong-role" => "{\"userId\":73,\"email\":\"synthetic@example.invalid\",\"role\":\"ADMIN\",\"canAuthenticate\":true}",
                    "missing-state" => "{\"userId\":73,\"email\":\"synthetic@example.invalid\",\"role\":\"ROLE_USER\"}",
                    "null-state" => Valid.Replace("true", "null"),
                    "string-state" => Valid.Replace("true", "\"true\""),
                    "string-id" => Valid.Replace("73", "\"73\""),
                    "duplicate-field" => Valid[..^1] + ",\"userId\":73}",
                    "extra-field" => Valid[..^1] + ",\"roles\":[\"ROLE_ADMIN\"]}",
                    "malformed" => "{broken-json",
                    "oversize" => new string(' ', HttpChatIdentityResolver.MaximumResponseBytes) + Valid,
                    "disabled-account" => Valid.Replace("true", "false"),
                    _ => Valid
                };
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(payload, context.RequestAborted);
            });
            app.MapGet("/credential-sink", () => { host!.RedirectHits++; return "unexpected"; });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

            // client도 production DI/HttpClient handler를 통해 만들며 상대 서버만 테스트 소유다.
            var clientBuilder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
            clientBuilder.Logging.ClearProviders();
            var key = RandomNumberGenerator.GetBytes(32);
            var origin = configurationScenario switch
            {
                "remote-http" => "http://example.invalid",
                "embedded-path" => address + "/other",
                "userinfo" => address.Replace("://", "://synthetic@"),
                "query" => address + "?unexpected=1",
                "fragment" => address + "#unexpected",
                _ => address
            };
            clientBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:Identity:Enabled"] = (configurationScenario != "disabled").ToString(),
                ["Chat:Identity:BaseUrl"] = origin,
                ["Chat:Identity:TimeoutSeconds"] = "1",
                ["Chat:Identity:ServiceAuthentication:Issuer"] = "translacat-chat",
                ["Chat:Identity:ServiceAuthentication:Audience"] = "translacat-be",
                ["Chat:Identity:ServiceAuthentication:Service"] = "translacat-chat",
                ["Chat:Identity:ServiceAuthentication:Base64SigningKey"] = configurationScenario == "missing-key" ? "" : Convert.ToBase64String(key)
            });
            clientBuilder.Services.AddChatServiceAuthentication(clientBuilder.Configuration);
            clientBuilder.Services.AddSingleton<TimeProvider>(new ServiceAuthTimeProvider());
            var services = clientBuilder.Services.BuildServiceProvider();
            host = new(app, services)
            {
                SigningSettings = new ChatServiceTokenSettings
                {
                    Issuer = "translacat-chat",
                    Audience = "translacat-be",
                    Service = "translacat-chat",
                    Base64SigningKey = Convert.ToBase64String(key)
                }
            };
            return host;
        }

        private const string Valid = "{\"userId\":73,\"email\":\"synthetic@example.invalid\",\"role\":\"ROLE_USER\",\"canAuthenticate\":true}";

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }
}
