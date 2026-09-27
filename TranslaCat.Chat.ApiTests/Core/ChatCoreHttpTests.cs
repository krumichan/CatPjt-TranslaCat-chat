using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TranslaCat.Chat.Api.Core;
using TranslaCat.Chat.Api.Membership;
using TranslaCat.Chat.Api.MembershipQuery;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Application.MembershipQuery;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Core;

namespace TranslaCat.Chat.ApiTests.Core;

public sealed class ChatCoreHttpTests
{
    private const long Id = 9007199254740993L;
    private const string ObjectKey = "open-chat-profiles/73/00000000-0000-0000-0000-000000000001.png";

    [Fact]
    public async Task Actual_HTTP_preserves_long_ids_and_distinct_profile_projections()
    {
        // 준비: production DI/client/signer를 조립하고 계정 서버만 합성 loopback으로 대체한다.
        await using var host = await CoreHost.StartAsync();
        var directory = host.Services.GetRequiredService<HttpChatCoreDirectory>();

        // 실행
        var message = await directory.GetUserAsync(Id, default);
        var direct = await directory.GetDirectPartnerAsync(Id, default);
        var summary = await directory.GetSummaryAsync(Id, default);
        var notification = await directory.GetDisplayAsync(Id, default);
        var member = await directory.FindByPublicIdAsync("public-large", default);
        var publicId = await directory.FindPublicIdAsync(Id, default);
        var aiName = await directory.GetUserNameAsync(Id, default);
        var audit = await directory.GetAuditIdentityAsync(Id, default);

        // 검증: 실제 profile이 없어도 summary의 기본값을 모든 기능으로 퍼뜨리지 않는다.
        Assert.Equal(Id, message.UserId);
        Assert.Equal(" ", message.Name);
        Assert.Equal("synthetic@example.invalid", audit);
        Assert.Equal("public-large", direct.DisplayName);
        Assert.Equal("email-prefix", summary.Nickname);
        Assert.Equal("public-large", notification.DisplayName);
        Assert.Equal("email-prefix", member!.Nickname);
        Assert.Equal("public-large", publicId);
        Assert.Equal(" ", aiName);
        Assert.Null(direct.Online);
        Assert.Null(message.ProfileImageUrl);
        Assert.All(host.Requests, request => Assert.Equal("POST", request.Method));
        Assert.All(host.Requests, request => Assert.Equal("/internal/v1/chat/accounts/lookup", request.Path));
        using var body = JsonDocument.Parse(host.Requests.First().Body);
        Assert.Equal(Id, body.RootElement.GetProperty("userIds")[0].GetInt64());
    }

    [Fact]
    public async Task Actual_profile_preserves_blank_notification_and_direct_fallback()
    {
        // 준비
        await using var host = await CoreHost.StartAsync("blank-profile");
        var directory = host.Services.GetRequiredService<HttpChatCoreDirectory>();

        // 실행
        var direct = await directory.GetDirectPartnerAsync(Id, default);
        var notification = await directory.GetDisplayAsync(Id, default);

        // 검증: legacy nullable/blank를 임의 default로 치환하지 않는다.
        Assert.Equal("public-large", direct.DisplayName);
        Assert.Equal(" ", notification.DisplayName);
        Assert.Null(direct.Bio);
    }

    [Fact]
    public async Task Missing_account_remains_explicit_without_fake_user_or_profile()
    {
        // 준비
        await using var host = await CoreHost.StartAsync("missing");
        var directory = host.Services.GetRequiredService<HttpChatCoreDirectory>();

        // 실행
        var member = await directory.FindByIdAsync(Id, default);
        var name = await directory.GetUserNameAsync(Id, default);
        var error = await Assert.ThrowsAsync<ChatRoomException>(() => directory.EnsureUserExistsAsync(Id, default));

        // 검증
        Assert.Null(member);
        Assert.Null(name);
        Assert.Equal("USER_NOT_FOUND", error.Code);
    }

    [Fact]
    public async Task Relation_and_image_operations_preserve_scope_method_body_and_raw_bytes()
    {
        // 준비
        await using var host = await CoreHost.StartAsync();
        var directory = host.Services.GetRequiredService<HttpChatCoreDirectory>();
        var storage = host.Services.GetRequiredService<HttpChatCoreStorage>();
        byte[] image = [137, 80, 78, 71, 13, 10, 26, 10];

        // 실행
        Assert.True(await directory.AreFriendsAsync(Id, 74, default));
        Assert.False(await directory.IsBlockedBetweenAsync(Id, 74, default));
        Assert.Equal("FRIEND", await directory.GetFriendStatusAsync(Id, 74, default));
        await storage.StoreAsync(ObjectKey, "image/png", image, default);
        Assert.Equal("https://synthetic.invalid/image.png", await storage.ResolveUrlAsync(ObjectKey, default));
        await storage.DeleteAsync(ObjectKey, default);

        // 검증
        var upload = Assert.Single(host.Requests, request => request.Method == "PUT");
        Assert.Equal(ObjectKey, upload.ObjectKey);
        Assert.Equal(image, upload.Body);
        Assert.Equal("image/png", upload.ContentType);
        var deletion = Assert.Single(host.Requests, request => request.Method == "DELETE");
        Assert.Contains(ObjectKey, Encoding.UTF8.GetString(deletion.Body));
        foreach (var request in host.Requests)
        {
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(request.Token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "translacat-chat",
                ValidateAudience = true,
                ValidAudience = "translacat-be",
                IssuerSigningKey = new SymmetricSecurityKey(host.Key),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
            });
            Assert.True(result.IsValid);
            var jwt = new JsonWebToken(request.Token);
            Assert.Equal("translacat-chat", jwt.Subject);
            Assert.Equal("chat-core-service", jwt.GetPayloadValue<string>("tokenUse"));
            Assert.Equal("Development", jwt.GetPayloadValue<string>("environment"));
            Assert.Equal(120, jwt.ValidTo.Subtract(jwt.IssuedAt).TotalSeconds);
            Assert.False(jwt.TryGetPayloadValue<string>("roles", out _));
            var expected = request.Method == "PUT" ? "chat:storage:write" : request.Method == "DELETE" ? "chat:storage:delete"
                : request.Path.EndsWith("/urls") ? "chat:storage:read" : "chat:relations:read";
            Assert.Equal(new[] { expected }, jwt.GetPayloadValue<string[]>("scopes"));
        }
    }

    [Theory]
    [InlineData("status-404")]
    [InlineData("status-500")]
    [InlineData("redirect")]
    [InlineData("malformed")]
    [InlineData("missing-field")]
    [InlineData("duplicate")]
    [InlineData("unknown-field")]
    [InlineData("mismatched-id")]
    [InlineData("string-id")]
    [InlineData("null-account")]
    [InlineData("null-list")]
    [InlineData("oversize")]
    public async Task Invalid_remote_response_is_sanitized_without_retry_or_redirect(string scenario)
    {
        // 준비
        await using var host = await CoreHost.StartAsync(scenario);
        var directory = host.Services.GetRequiredService<HttpChatCoreDirectory>();

        // 실행
        var error = await Assert.ThrowsAsync<ChatCoreUnavailableException>(() => directory.GetUserAsync(Id, default));

        // 검증
        Assert.Equal("Chat Core dependency is unavailable.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Single(host.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_is_unavailable_but_caller_cancellation_remains_cancellation(bool callerCancel)
    {
        // 준비
        await using var host = await CoreHost.StartAsync("delay");
        using var cancellation = new CancellationTokenSource();
        var directory = host.Services.GetRequiredService<HttpChatCoreDirectory>();
        var action = directory.GetUserAsync(Id, cancellation.Token);
        await host.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 실행
        if (callerCancel)
        {
            cancellation.Cancel();
        }

        var error = await Record.ExceptionAsync(() => action);

        // 검증
        if (callerCancel)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
        }
        else
        {
            Assert.IsType<ChatCoreUnavailableException>(error);
        }

        Assert.Single(host.Requests);
    }

    [Theory]
    [InlineData("null-relation")]
    [InlineData("invalid-relation")]
    [InlineData("null-object")]
    public async Task Invalid_semantic_projection_is_unavailable_instead_of_null_reference_failure(string scenario)
    {
        // 준비
        await using var host = await CoreHost.StartAsync(scenario);
        var directory = host.Services.GetRequiredService<HttpChatCoreDirectory>();
        var storage = host.Services.GetRequiredService<HttpChatCoreStorage>();

        // 실행
        var error = scenario == "null-object"
            ? await Record.ExceptionAsync(() => storage.ResolveUrlAsync(ObjectKey, default))
            : await Record.ExceptionAsync(() => directory.GetFriendStatusAsync(Id, 74, default));

        // 검증
        Assert.IsType<ChatCoreUnavailableException>(error);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Storage_failure_is_not_retried_and_does_not_imply_object_or_DB_commit_outcome()
    {
        // 준비
        await using var host = await CoreHost.StartAsync("status-500");
        var storage = host.Services.GetRequiredService<HttpChatCoreStorage>();

        // 실행
        var error = await Record.ExceptionAsync(() => storage.StoreAsync(ObjectKey, "image/png", new byte[] { 137, 80 }, default));

        // 검증: 실제 byte 전송 실패만 확인한다. 여기에는 DB transaction이나 외부 object store가 없다.
        Assert.IsType<ChatCoreUnavailableException>(error);
        Assert.Single(host.Requests);
    }

    [Fact]
    public async Task Actual_member_HTTP_pipeline_maps_Core_failure_before_feature_generic_500_filter()
    {
        // 준비: 실제 member Controller/Application/profile adapter; auth와 CHAT DB 상태만 테스트 대역이다.
        await using var remote = await CoreHost.StartAsync("status-500");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Configuration.AddInMemoryCollection(CoreHost.Configuration(remote.Origin, remote.Key));
        builder.Services.AddChatReadHttp();
        builder.Services.AddChatMembershipHttp();
        builder.Services.AddChatMemberQueryHttp();
        builder.Services.AddSingleton<IChatMemberQueryStore, SyntheticMemberStore>();
        builder.Services.AddChatCore(builder.Configuration, "Development");
        builder.Services.AddAuthentication(TestReadAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestReadAuthenticationHandler>(TestReadAuthenticationHandler.SchemeName, _ => { });
        await using var app = builder.Build();
        app.UseChatReadHttp();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(Address(app)) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/chat/rooms/41/members");
        request.Headers.Add(TestReadAuthenticationHandler.HeaderName, "73");

        // 실행
        using var response = await client.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // 검증: routing/auth/binding/Application/외부 HTTP/MVC filter/serialization을 통과했다.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("CHAT_CORE_UNAVAILABLE", json.RootElement.GetProperty("body").GetProperty("errorCode").GetString());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Single(remote.Requests);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("http://example.invalid", "Development")]
    [InlineData("http://127.0.0.1:5000", "Production")]
    [InlineData("https://example.invalid/path", "Production")]
    [InlineData("https://user@example.invalid", "Production")]
    [InlineData("https://example.invalid?key=value", "Production")]
    [InlineData("https://example.invalid/#fragment", "Production")]
    public void Enabled_configuration_rejects_unsafe_origin_before_port_registration(string origin, string environment)
    {
        // 준비
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(CoreHost.Configuration(origin, RandomNumberGenerator.GetBytes(32))).Build();
        // 실행
        var error = Assert.Throws<InvalidOperationException>(() => services.AddChatCore(configuration, environment));
        // 검증
        Assert.DoesNotContain(origin, error.Message);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(HttpChatCoreDirectory));
    }

    [Fact]
    public void Disabled_configuration_registers_no_Core_capability()
    {
        // 준비
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:Core:Enabled"] = "false" }).Build();
        // 실행
        services.AddChatCore(configuration, "Production");
        // 검증
        Assert.Empty(services);
    }

    private sealed class SyntheticMemberStore : IChatMemberQueryStore
    {
        public Task<ChatMemberQueryState> ReadAsync(long userId, long roomId, long? targetUserId, CancellationToken token)
        {
            return Task.FromResult(new ChatMemberQueryState(false, [new(7, 41, Id, "MEMBER", true, new DateTime(2026, 1, 1), null)], [], null));
        }
    }

    private static string Address(WebApplication app)
    {
        return app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }

    private sealed record Captured(string Method, string Path, string Token, string ObjectKey, string ContentType, byte[] Body);

    private sealed class CoreHost(WebApplication app, ServiceProvider services, string origin, byte[] key) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public string Origin { get; } = origin;
        public byte[] Key { get; } = key;
        public ConcurrentQueue<Captured> Requests { get; } = new();
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static Dictionary<string, string?> Configuration(string origin, byte[] key)
        {
            return new()
            {
                ["Chat:Core:Enabled"] = "true",
                ["Chat:Core:BaseUrl"] = origin,
                ["Chat:Core:TimeoutSeconds"] = "1",
                ["Chat:Core:ServiceAuthentication:Issuer"] = "translacat-chat",
                ["Chat:Core:ServiceAuthentication:Audience"] = "translacat-be",
                ["Chat:Core:ServiceAuthentication:Service"] = "translacat-chat",
                ["Chat:Core:ServiceAuthentication:Base64SigningKey"] = Convert.ToBase64String(key)
            };
        }

        public static async Task<CoreHost> StartAsync(string scenario = "valid")
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            CoreHost? host = null;
            app.Run(async context =>
            {
                using var memory = new MemoryStream();
                await context.Request.Body.CopyToAsync(memory, context.RequestAborted);
                var path = context.Request.Path.Value!;
                host!.Requests.Enqueue(new(context.Request.Method, path, context.Request.Headers.Authorization.ToString()[7..],
                    context.Request.Headers["X-Chat-Object-Key"].ToString(), context.Request.ContentType ?? "", memory.ToArray()));
                host.Arrived.TrySetResult();
                if (scenario == "delay")
                {
                    await Task.Delay(5000, context.RequestAborted);
                    return;
                }
                if (scenario.StartsWith("status-"))
                {
                    context.Response.StatusCode = int.Parse(scenario[7..]);
                    return;
                }
                if (scenario == "redirect")
                {
                    context.Response.Redirect("/credential-sink");
                    return;
                }
                if (context.Request.Method is "PUT" or "DELETE")
                {
                    context.Response.StatusCode = 204;
                    return;
                }
                if (path.EndsWith("/relations/query"))
                {
                    if (scenario is "null-relation" or "invalid-relation")
                    {
                        var relation = scenario == "null-relation" ? "null" : "{\"targetUserId\":74,\"friends\":false,\"blocked\":false,\"friendStatus\":\"UNKNOWN\"}";
                        await context.Response.WriteAsync("{\"relations\":[" + relation + "],\"missingUserIds\":[]}");
                        return;
                    }
                    await context.Response.WriteAsJsonAsync(new ChatCoreRelations([new(74, true, false, "FRIEND")], []));
                    return;
                }
                if (path.EndsWith("/storage/urls"))
                {
                    if (scenario == "null-object")
                    {
                        await context.Response.WriteAsync("{\"objects\":[null]}");
                        return;
                    }
                    await context.Response.WriteAsJsonAsync(new ChatCoreObjectUrls([new(ObjectKey, "https://synthetic.invalid/image.png")]));
                    return;
                }
                ChatCoreProfile? profile = scenario == "blank-profile" ? new(" ", null, null, " ") : null;
                var account = new ChatCoreAccount(Id, "synthetic@example.invalid", " ", "public-large", profile,
                    new(Id, "public-large", "email-prefix", null, null, null));
                var body = JsonSerializer.Serialize(new ChatCoreAccounts([account], [], []), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                body = scenario switch
                {
                    "missing" => JsonSerializer.Serialize(new ChatCoreAccounts([], [Id], []), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    "malformed" => "{broken",
                    "missing-field" => "{\"accounts\":[]}",
                    "duplicate" => body[..^1] + ",\"accounts\":[]}",
                    "unknown-field" => body[..^1] + ",\"roles\":[\"ADMIN\"]}",
                    "mismatched-id" => body.Replace(Id.ToString(), "74"),
                    "string-id" => body.Replace(":9007199254740993", ":\"9007199254740993\""),
                    "null-account" => "{\"accounts\":[null],\"missingUserIds\":[],\"missingPublicIds\":[]}",
                    "null-list" => "{\"accounts\":null,\"missingUserIds\":[],\"missingPublicIds\":[]}",
                    "oversize" => new string(' ', HttpChatCoreClient.MaximumResponseBytes) + body,
                    _ => body
                };
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(body, context.RequestAborted);
            });
            await app.StartAsync();
            var key = RandomNumberGenerator.GetBytes(32);
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.ClearProviders());
            services.AddSingleton(TimeProvider.System);
            var config = new ConfigurationBuilder().AddInMemoryCollection(Configuration(Address(app), key)).Build();
            services.AddChatCore(config, "Development");
            host = new(app, services.BuildServiceProvider(), Address(app), key);
            return host;
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
