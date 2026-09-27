using System.Collections.Concurrent;
using System.Net;
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
using TranslaCat.Chat.Api.Realtime;
using TranslaCat.Chat.Application.Realtime;

namespace TranslaCat.Chat.ApiTests.Realtime;

internal sealed class ChatRealtimeTestHost(WebApplication application, Uri address, byte[] key, SyntheticRealtimePorts ports) : IAsyncDisposable
{
    public ChatRealtimeBroker Broker => application.Services.GetRequiredService<ChatRealtimeBroker>();
    public SyntheticRealtimePorts Ports { get; } = ports;

    public static async Task<ChatRealtimeTestHost> StartAsync(bool registerAccess = true, bool registerSender = true)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Chat:Authentication:Enabled"] = "true",
            ["Chat:Authentication:Base64SigningKey"] = Convert.ToBase64String(key)
        });
        builder.Services.AddChatJwtAuthentication(builder.Configuration);
        builder.Services.AddChatRealtime();
        builder.Services.AddSingleton<TimeProvider>(new FixedReadTimeProvider());

        // 실제 JWT, WebSocket, STOMP codec/broker를 사용한다. 계정 조회/업무 저장/Presence만 합성 port다.
        var ports = new SyntheticRealtimePorts();
        builder.Services.AddSingleton<IChatIdentityResolver>(ports);
        builder.Services.AddSingleton<IChatRealtimeSessionLifecycle>(ports);
        if (registerAccess)
        {
            builder.Services.AddSingleton<IChatRealtimeAccess>(ports);
        }
        if (registerSender)
        {
            builder.Services.AddSingleton<IChatRealtimeMessageSender>(ports);
        }

        var app = builder.Build();
        app.MapChatRealtime();
        await app.StartAsync();
        var httpAddress = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var address = new UriBuilder(httpAddress) { Scheme = "ws", Path = "/ws/chat" }.Uri;
        return new ChatRealtimeTestHost(app, address, key, ports);
    }

    public string CreateToken(long userId = 73, bool invalidSignature = false)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = $"synthetic-{userId}@example.invalid",
            ["id"] = userId,
            ["iat"] = FixedReadTimeProvider.Now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["exp"] = FixedReadTimeProvider.Now.AddHours(1).ToUnixTimeSeconds()
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(
            JsonSerializer.Serialize(claims),
            new SigningCredentials(new SymmetricSecurityKey(invalidSignature ? RandomNumberGenerator.GetBytes(32) : key), SecurityAlgorithms.HmacSha256));
    }

    public async Task<StompTestClient> OpenAsync()
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("v12.stomp");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(address, timeout.Token);
        return new StompTestClient(socket);
    }

    public async Task<StompTestClient> ConnectAsync(long userId = 73)
    {
        var client = await OpenAsync();
        await client.SendAsync($"CONNECT\naccept-version:1.2,1.1\nAuthorization:Bearer {CreateToken(userId)}\nheart-beat:10000,10000\n\n\0");
        var connected = await client.ReceiveAsync();
        Assert.StartsWith("CONNECTED\n", connected);
        Assert.Contains("version:1.2\n", connected);
        Assert.Contains("heart-beat:0,0\n", connected);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await application.StopAsync();
        await application.DisposeAsync();
    }
}

internal sealed class SyntheticRealtimePorts : IChatIdentityResolver, IChatRealtimeAccess, IChatRealtimeMessageSender, IChatRealtimeSessionLifecycle
{
    public ConcurrentDictionary<long, bool> DeniedUsers { get; } = [];
    public ConcurrentQueue<(long UserId, long RoomId, string Content)> Sent { get; } = new();
    public ConcurrentQueue<(string SessionId, long UserId)> Connected { get; } = new();
    public ConcurrentQueue<(string SessionId, long UserId)> Disconnected { get; } = new();
    public TaskCompletionSource DisconnectObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool FailAfterSessionRegistration
    {
        get; set;
    }
    public Exception? SendFailure
    {
        get; set;
    }

    public Task<ChatResolvedIdentity?> ResolveAsync(ChatIdentityLookup lookup, CancellationToken cancellationToken)
    {
        return Task.FromResult<ChatResolvedIdentity?>(new(lookup.TokenUserId, lookup.Subject, "ROLE_USER", true));
    }

    public Task ValidateRoomAccessAsync(long userId, long roomId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DeniedUsers.ContainsKey(userId) || roomId != 41)
        {
            throw new UnauthorizedAccessException("Synthetic membership denied.");
        }
        return Task.CompletedTask;
    }

    public Task SendTextAsync(long userId, long roomId, string content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (SendFailure is not null)
        {
            throw SendFailure;
        }
        Sent.Enqueue((userId, roomId, content));
        return Task.CompletedTask;
    }

    public Task ConnectAsync(string sessionId, long userId, CancellationToken cancellationToken)
    {
        Connected.Enqueue((sessionId, userId));
        if (FailAfterSessionRegistration)
        {
            throw new InvalidOperationException("Synthetic partial session registration failure.");
        }
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string sessionId, long userId, CancellationToken cancellationToken)
    {
        Disconnected.Enqueue((sessionId, userId));
        DisconnectObserved.TrySetResult();
        return Task.CompletedTask;
    }
}

internal sealed class StompTestClient(ClientWebSocket socket) : IDisposable
{
    public ClientWebSocket Socket { get; } = socket;

    public async Task SendAsync(string frame, int? fragmentAt = null)
    {
        var bytes = Encoding.UTF8.GetBytes(frame);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (fragmentAt is { } offset)
        {
            await Socket.SendAsync(bytes.AsMemory(0, offset), WebSocketMessageType.Text, false, timeout.Token);
            await Socket.SendAsync(bytes.AsMemory(offset), WebSocketMessageType.Text, true, timeout.Token);
        }
        else
        {
            await Socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        }
    }

    public async Task<string> ReceiveAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        ValueWebSocketReceiveResult received;
        do
        {
            received = await Socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            stream.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public async Task SubscribeAsync(string id, string destination)
    {
        await SendAsync($"SUBSCRIBE\nid:{id}\ndestination:{destination}\nreceipt:sub-{id}\n\n\0");
        Assert.Contains($"receipt-id:sub-{id}\n", await ReceiveAsync());
    }

    public async Task AssertBarrierAsync(string id)
    {
        // receipt는 앞선 server publish 완료 뒤 보낸 frame의 응답이다. 메시지 부재를 sleep으로 판정하지 않는다.
        await SendAsync($"UNSUBSCRIBE\nid:not-registered\nreceipt:{id}\n\n\0");
        Assert.Contains($"receipt-id:{id}\n", await ReceiveAsync());
    }

    public void Dispose()
    {
        Socket.Abort();
        Socket.Dispose();
    }
}
