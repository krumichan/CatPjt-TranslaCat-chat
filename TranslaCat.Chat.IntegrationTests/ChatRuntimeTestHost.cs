using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TranslaCat.Chat.Api.Authentication;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Realtime;
using TranslaCat.Chat.Api.Runtime;

namespace TranslaCat.Chat.IntegrationTests;

public sealed class ChatRuntimeTestHost : IAsyncDisposable
{
    private readonly WebApplication application;
    private readonly byte[] signingKey;
    public HttpClient Client
    {
        get;
    }
    public IServiceProvider Services => application.Services;

    private ChatRuntimeTestHost(WebApplication application, Uri address, byte[] key)
    {
        this.application = application;
        signingKey = key;
        Client = new()
        {
            BaseAddress = address,
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public static async Task<ChatRuntimeTestHost> StartAsync(ChatMySqlFixture fixture, string redisNamespace,
        bool identityConfigured = true, bool databaseConfigured = true, bool redisConfigured = true,
        Action<IServiceCollection>? configure = null)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var settings = new Dictionary<string, string?>
        {
            ["Chat:Authentication:Enabled"] = "true",
            ["Chat:Authentication:Base64SigningKey"] = Convert.ToBase64String(key),
            ["Chat:SourceTimeZone"] = "UTC",
            ["Chat:Presence:Enabled"] = "true",
            ["Chat:Presence:SessionTtl"] = "00:00:03",
            ["Chat:Presence:RefreshInterval"] = "00:00:00.500",
            ["Chat:Presence:OfflineGrace"] = "00:00:00.300"
        };
        if (redisConfigured)
        {
            settings["Chat:Redis:ConnectionString"] = Environment.GetEnvironmentVariable("CHAT_TEST_REDIS")
                ?? throw new InvalidOperationException("Owned Redis runtime is required.");
            settings["Chat:Redis:Namespace"] = redisNamespace;
        }
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddChatReadHttp();
        builder.Services.AddChatRuntime(builder.Configuration);
        if (databaseConfigured)
        {
            builder.Services.AddChatDatabase(fixture.ConnectionString, isolatedTestCatalog: true);
        }

        // 실제 JWT/HTTP/EF/Redis/STOMP를 사용한다. 계정 소유 서비스만 이 테스트 구성에서 대체한다.
        if (identityConfigured)
        {
            builder.Services.AddSingleton<IChatIdentityResolver, SyntheticIdentityResolver>();
        }
        configure?.Invoke(builder.Services);
        var app = builder.Build();
        app.UseChatReadHttp();
        app.MapChatRealtime();
        app.MapChatReadiness();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var host = new ChatRuntimeTestHost(app, new Uri(address), key);
        if (redisConfigured)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!app.Services.GetRequiredService<ChatRealtimeRedisRelay>().IsSubscribed)
            {
                await Task.Delay(20, deadline.Token);
            }
        }
        return host;
    }

    public string CreateToken(long userId)
    {
        // 고정 시각을 등록한 회귀도 실제 JWT 검증과 같은 clock으로 유효 기간을 만든다.
        var now = Services.GetRequiredService<TimeProvider>().GetUtcNow();
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["sub"] = $"synthetic-{userId}@example.invalid",
            ["id"] = userId,
            ["iat"] = now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds()
        });
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(payload,
            new SigningCredentials(new SymmetricSecurityKey(signingKey), SecurityAlgorithms.HmacSha256));
    }

    public async Task<HttpResponseMessage> MarkReadAsync(long userId, long roomId, long messageId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/chat/rooms/{roomId}/read");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(userId));
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            lastReadMessageId = messageId
        }), Encoding.UTF8, "application/json");
        return await Client.SendAsync(request);
    }

    public async Task<ChatRuntimeSocket> ConnectAsync(long userId)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("v12.stomp");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new UriBuilder(Client.BaseAddress!) { Scheme = "ws", Path = "/ws/chat" }.Uri, deadline.Token);
        var client = new ChatRuntimeSocket(socket);
        await client.SendAsync($"CONNECT\naccept-version:1.2\nAuthorization:Bearer {CreateToken(userId)}\n\n\0");
        Assert.StartsWith("CONNECTED\n", await client.ReceiveAsync());
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
    }

    private sealed class SyntheticIdentityResolver : IChatIdentityResolver
    {
        public Task<ChatResolvedIdentity?> ResolveAsync(ChatIdentityLookup lookup, CancellationToken cancellationToken)
        {
            return Task.FromResult<ChatResolvedIdentity?>(new(lookup.TokenUserId, lookup.Subject, "ROLE_USER", true));
        }
    }
}

public sealed class ChatRuntimeSocket : IDisposable
{
    private readonly ClientWebSocket socket;
    private readonly Queue<string> pendingMessages = new();
    public ChatRuntimeSocket(ClientWebSocket socket)
    {
        this.socket = socket;
    }

    public async Task SendAsync(string frame)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.SendAsync(Encoding.UTF8.GetBytes(frame).AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
    }

    public async Task<string> ReceiveAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var content = new MemoryStream();
        var buffer = new byte[4096];
        ValueWebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer.AsMemory(), deadline.Token);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            content.Write(buffer, 0, received.Count);
        } while (!received.EndOfMessage);
        return Encoding.UTF8.GetString(content.ToArray());
    }

    public async Task SubscribeAsync(string id, string destination)
    {
        await SendAsync($"SUBSCRIBE\nid:{id}\ndestination:{destination}\nreceipt:{id}\n\n\0");
        while (true)
        {
            var frame = await ReceiveAsync();
            if (frame.StartsWith("MESSAGE\n", StringComparison.Ordinal))
            {
                pendingMessages.Enqueue(frame);
                continue;
            }
            Assert.Contains($"receipt-id:{id}\n", frame);
            return;
        }
    }

    public async Task<JsonElement> ReceiveEventAsync(string eventType)
    {
        // 온라인 알림과 업무 이벤트가 교차해도 원하는 실제 eventType을 검사한다.
        for (var index = 0; index < 10; index++)
        {
            var frame = pendingMessages.Count > 0 ? pendingMessages.Dequeue() : await ReceiveAsync();
            Assert.StartsWith("MESSAGE\n", frame);
            var body = frame[(frame.IndexOf("\n\n", StringComparison.Ordinal) + 2)..].TrimEnd('\0');
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.GetProperty("eventType").GetString() == eventType)
            {
                return document.RootElement.Clone();
            }
        }
        throw new InvalidOperationException("Expected event was not received within bounded frames.");
    }

    public void Dispose()
    {
        socket.Abort();
        socket.Dispose();
    }
}
