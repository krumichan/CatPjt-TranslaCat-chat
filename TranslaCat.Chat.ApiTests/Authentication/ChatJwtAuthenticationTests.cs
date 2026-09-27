using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TranslaCat.Chat.Api.Authentication;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.Read;

namespace TranslaCat.Chat.ApiTests.Authentication;

public sealed class ChatJwtAuthenticationTests
{
    private static readonly DateTimeOffset Now = FixedReadTimeProvider.Now;

    [Theory]
    [InlineData(SecurityAlgorithms.HmacSha256, 32)]
    [InlineData(SecurityAlgorithms.HmacSha384, 48)]
    [InlineData(SecurityAlgorithms.HmacSha512, 64)]
    public async Task Signed_BE_claims_use_current_identity_after_actual_HMAC_validation(string algorithm, int keyLength)
    {
        // 준비: 실제 JWT 서명을 만들되 사용자/비밀은 모두 이 테스트의 합성 값이다.
        var key = RandomNumberGenerator.GetBytes(keyLength);
        var resolver = new SyntheticIdentityResolver();
        var authenticator = CreateAuthenticator(key, resolver);
        var token = CreateToken(key, algorithm);

        // 실행
        var principal = await authenticator.AuthenticateAsync(token);

        // 검증: 원본 id 대신 현재 계정 조회와 일치한 신원만 인증된다.
        Assert.NotNull(principal);
        Assert.True(principal.Identity!.IsAuthenticated);
        Assert.Equal("73", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("synthetic@example.invalid", principal.Identity.Name);
        Assert.True(principal.IsInRole("ROLE_USER"));
        Assert.Single(resolver.Lookups);
        Assert.Equal(new ChatIdentityLookup("synthetic@example.invalid", 73), resolver.Lookups[0]);
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("unsigned")]
    [InlineData("malformed")]
    [InlineData("expired")]
    [InlineData("future-not-before")]
    [InlineData("missing-expiration")]
    [InlineData("missing-subject")]
    [InlineData("missing-id")]
    [InlineData("invalid-id")]
    public async Task Invalid_signature_lifetime_or_identity_claims_fail_before_shared_lookup(string failure)
    {
        // 준비
        var key = RandomNumberGenerator.GetBytes(32);
        var resolver = new SyntheticIdentityResolver();
        var claims = CreateClaims();
        var signingKey = key;
        switch (failure)
        {
            case "wrong-key":
                signingKey = RandomNumberGenerator.GetBytes(32);
                break;
            case "expired":
                claims["exp"] = Now.AddSeconds(-1).ToUnixTimeSeconds();
                break;
            case "future-not-before":
                claims["nbf"] = Now.AddMinutes(1).ToUnixTimeSeconds();
                break;
            case "missing-expiration":
                claims.Remove("exp");
                break;
            case "missing-subject":
                claims.Remove("sub");
                break;
            case "missing-id":
                claims.Remove("id");
                break;
            case "invalid-id":
                claims["id"] = "73.5";
                break;
        }
        var token = failure switch
        {
            "malformed" => "synthetic-malformed-token",
            "unsigned" => new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(JsonSerializer.Serialize(claims)),
            _ => CreateToken(signingKey, SecurityAlgorithms.HmacSha256, claims)
        };

        // 실행
        var principal = await CreateAuthenticator(key, resolver).AuthenticateAsync(token);

        // 검증
        Assert.Null(principal);
        Assert.Empty(resolver.Lookups);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("different-id")]
    [InlineData("different-subject")]
    [InlineData("unknown-role")]
    [InlineData("unavailable")]
    public async Task Current_account_absence_state_mismatch_or_lookup_failure_does_not_authenticate(string failure)
    {
        // 준비
        var key = RandomNumberGenerator.GetBytes(32);
        var resolver = new SyntheticIdentityResolver();
        resolver.Identity = failure switch
        {
            "missing" => null,
            "disabled" => resolver.Identity! with { CanAuthenticate = false },
            "different-id" => resolver.Identity! with { UserId = 74 },
            "different-subject" => resolver.Identity! with { Email = "other@example.invalid" },
            "unknown-role" => resolver.Identity! with { Role = "UNRECOGNIZED" },
            _ => resolver.Identity
        };
        resolver.Fail = failure == "unavailable";
        var token = CreateToken(key);

        // 실행
        var principal = await CreateAuthenticator(key, resolver).AuthenticateAsync(token);

        // 검증
        Assert.Null(principal);
        Assert.Single(resolver.Lookups);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("no-resolver")]
    [InlineData("invalid-base64")]
    [InlineData("short-key")]
    public async Task Missing_configuration_or_shared_adapter_never_falls_back_to_token_identity(string failure)
    {
        // 준비
        var key = RandomNumberGenerator.GetBytes(32);
        var resolver = new SyntheticIdentityResolver();
        var settings = new ChatJwtOptions
        {
            Enabled = failure != "disabled",
            Base64SigningKey = failure switch
            {
                "invalid-base64" => "not-valid-base64",
                "short-key" => Convert.ToBase64String(new byte[8]),
                _ => Convert.ToBase64String(key)
            }
        };
        var authenticator = new ChatJwtAuthenticator(Options.Create(settings), new FixedReadTimeProvider(), failure == "no-resolver" ? null : resolver);

        // 실행
        var principal = await authenticator.AuthenticateAsync(CreateToken(key));

        // 검증
        Assert.Null(principal);
        Assert.Empty(resolver.Lookups);
    }

    [Fact]
    public async Task Cancellation_is_propagated_instead_of_becoming_authentication_success()
    {
        // 준비
        var key = RandomNumberGenerator.GetBytes(32);
        var resolver = new SyntheticIdentityResolver();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // 실행 / 검증
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateAuthenticator(key, resolver).AuthenticateAsync(CreateToken(key), cancellation.Token));
        Assert.Empty(resolver.Lookups);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_HTTP_bearer_handler_uses_crypto_and_shared_identity_boundary(bool tamperSignature)
    {
        // 준비: 테스트 인증 handler 대신 새 운영 handler를 실제 Kestrel에 등록한다.
        var key = RandomNumberGenerator.GetBytes(32);
        var resolver = new SyntheticIdentityResolver();
        await using var host = await JwtHttpHost.StartAsync(key, resolver);
        var token = CreateToken(tamperSignature ? RandomNumberGenerator.GetBytes(32) : key);
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/chat/rooms/41/read")
        {
            Content = new StringContent("{\"lastReadMessageId\":101}", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 실행
        using var response = await host.Client.SendAsync(request);
        var payload = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증: DB는 transaction fake이며 인증 암호 검증·HTTP pipeline은 실제 구현이다.
        Assert.Equal(tamperSignature ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(tamperSignature ? 0 : 1, host.Transaction.ExecuteCount);
        Assert.Equal(tamperSignature ? 0 : 1, resolver.Lookups.Count);
        Assert.DoesNotContain(token, payload.GetRawText());
        if (!tamperSignature)
        {
            Assert.Equal(73, host.Transaction.LoginUserId);
            Assert.Equal(101, payload.GetProperty("body").GetProperty("lastReadMessageId").GetInt64());
        }
    }

    [Fact]
    public async Task Actual_HTTP_valid_signature_is_rejected_when_shared_identity_adapter_is_absent()
    {
        // 준비
        var key = RandomNumberGenerator.GetBytes(32);
        await using var host = await JwtHttpHost.StartAsync(key, null);
        host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(key));

        // 실행
        using var response = await host.Client.PatchAsync("/api/v1/chat/rooms/41/read", new StringContent("{\"lastReadMessageId\":101}", System.Text.Encoding.UTF8, "application/json"));

        // 검증
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Transaction.ExecuteCount);
    }

    private static ChatJwtAuthenticator CreateAuthenticator(byte[] key, IChatIdentityResolver? resolver)
    {
        return new(Options.Create(new ChatJwtOptions { Enabled = true, Base64SigningKey = Convert.ToBase64String(key) }), new FixedReadTimeProvider(), resolver);
    }

    private static Dictionary<string, object> CreateClaims()
    {
        return new()
        {
            ["sub"] = "synthetic@example.invalid",
            ["id"] = 73L,
            ["iat"] = Now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["exp"] = Now.AddHours(1).ToUnixTimeSeconds()
        };
    }

    private static string CreateToken(byte[] key, string algorithm = SecurityAlgorithms.HmacSha256, Dictionary<string, object>? claims = null)
    {
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(JsonSerializer.Serialize(claims ?? CreateClaims()), new SigningCredentials(new SymmetricSecurityKey(key), algorithm));
    }

    private sealed class SyntheticIdentityResolver : IChatIdentityResolver
    {
        public ChatResolvedIdentity? Identity { get; set; } = new(73, "synthetic@example.invalid", "ROLE_USER", true);
        public bool Fail
        {
            get; set;
        }
        public List<ChatIdentityLookup> Lookups { get; } = [];

        public Task<ChatResolvedIdentity?> ResolveAsync(ChatIdentityLookup lookup, CancellationToken cancellationToken)
        {
            Lookups.Add(lookup);
            if (Fail)
            {
                throw new HttpRequestException("Synthetic shared identity service unavailable.");
            }

            return Task.FromResult(Identity);
        }
    }

    private sealed class JwtHttpHost(WebApplication application, HttpClient client, HttpReadTransaction transaction) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public HttpReadTransaction Transaction { get; } = transaction;

        public static async Task<JwtHttpHost> StartAsync(byte[] key, IChatIdentityResolver? resolver)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:Authentication:Enabled"] = "true",
                ["Chat:Authentication:Base64SigningKey"] = Convert.ToBase64String(key)
            });

            // 합성 identity와 저장소만 테스트 DI에 등록한다. 운영 인증 handler를 교체하지 않는다.
            builder.Services.AddChatReadHttp();
            builder.Services.AddChatJwtAuthentication(builder.Configuration);
            builder.Services.AddSingleton<TimeProvider>(new FixedReadTimeProvider());
            builder.Services.AddSingleton(new ChatReadLocalTime(TimeZoneInfo.Utc));
            var transaction = new HttpReadTransaction();
            builder.Services.AddSingleton<IChatReadTransaction>(transaction);
            if (resolver is not null)
            {
                builder.Services.AddSingleton(resolver);
            }

            var app = builder.Build();
            app.UseChatReadHttp();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new JwtHttpHost(app, new HttpClient { BaseAddress = new Uri(address) }, transaction);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }
}
