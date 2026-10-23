using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TranslaCat.Chat.Api.Authentication;
using TranslaCat.Chat.Api.Realtime;
using TranslaCat.Chat.Api.Safety;
using TranslaCat.Chat.Application.Realtime;
using TranslaCat.Chat.Application.Safety;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.Repositories;

namespace TranslaCat.Chat.IntegrationTests;

// 테스트 전용 Kestrel/STOMP 클라이언트. 계정 신원만 합성이며 room 권한/본문 정책은 실제 CHAT DB를 사용한다.
internal sealed class SafetyRealtimeHost(WebApplication application, Uri address, byte[] key) : IAsyncDisposable
{
    public ChatRealtimeBroker Broker => application.Services.GetRequiredService<ChatRealtimeBroker>();

    public static async Task<SafetyRealtimeHost> StartAsync(IDbContextFactory<ChatDbContext> contexts, IChatContentPolicy policy)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        byte[] key = RandomNumberGenerator.GetBytes(32);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Chat:Authentication:Enabled"] = "true", ["Chat:Authentication:Base64SigningKey"] = Convert.ToBase64String(key)
        });
        builder.Services.AddChatJwtAuthentication(builder.Configuration);
        builder.Services.AddChatRealtime();
        builder.Services.AddSingleton<IChatIdentityResolver, SyntheticIdentity>();
        builder.Services.AddSingleton<IChatRealtimeAccess>(new EfChatRealtimeAccess(contexts, policy));
        builder.Services.AddSingleton(new ChatRealtimeContentPolicy(contexts, policy));
        var app = builder.Build();
        app.MapChatRealtime();
        await app.StartAsync();
        string http = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(app, new UriBuilder(http) { Scheme = "ws", Path = "/ws/chat" }.Uri, key);
    }

    public async Task<SafetySocket> ConnectAsync(long userId, long roomId)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol("v12.stomp");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(address, timeout.Token);
        var client = new SafetySocket(socket);
        string token = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(JsonSerializer.Serialize(new
        {
            sub = $"safety-{userId}@example.invalid", id = userId,
            iat = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(), exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        }), new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256));
        await client.SendAsync($"CONNECT\naccept-version:1.2\nAuthorization:Bearer {token}\n\n\0");
        Assert.StartsWith("CONNECTED\n", await client.ReceiveAsync());
        await client.SendAsync($"SUBSCRIBE\nid:safety\ndestination:/topic/chat/rooms/{roomId}\nreceipt:sub\n\n\0");
        Assert.Contains("receipt-id:sub\n", await client.ReceiveAsync());
        return client;
    }

    public async ValueTask DisposeAsync() { await application.StopAsync(); await application.DisposeAsync(); }

    private sealed class SyntheticIdentity : IChatIdentityResolver
    {
        public Task<ChatResolvedIdentity?> ResolveAsync(ChatIdentityLookup lookup, CancellationToken token)
            => Task.FromResult<ChatResolvedIdentity?>(new(lookup.TokenUserId, lookup.Subject, "ROLE_USER", true));
    }
}

internal sealed class SafetySocket(ClientWebSocket socket) : IDisposable
{
    public async Task SendAsync(string frame)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.SendAsync(Encoding.UTF8.GetBytes(frame).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
    }

    public async Task<string> ReceiveAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stream = new MemoryStream();
        byte[] bytes = new byte[4096];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(bytes.AsMemory(), timeout.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            stream.Write(bytes, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public async Task AssertBarrierAsync(string id)
    {
        // 앞선 publish await 뒤 receipt를 읽어 미전달을 확인한다. 취소 타이머로 socket을 망가뜨리지 않는다.
        await SendAsync($"UNSUBSCRIBE\nid:not-registered\nreceipt:{id}\n\n\0");
        Assert.Contains($"receipt-id:{id}\n", await ReceiveAsync());
    }

    public async Task SubscribeAsync(string id, string destination)
    {
        await SendAsync($"SUBSCRIBE\nid:{id}\ndestination:{destination}\nreceipt:{id}\n\n\0");
        Assert.Contains($"receipt-id:{id}\n", await ReceiveAsync());
    }

    public void Dispose() { socket.Abort(); socket.Dispose(); }
}
