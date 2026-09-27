using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
using TranslaCat.Chat.Api.ServiceAuthentication;
using TranslaCat.Chat.Application.Realtime;

if (Environment.GetEnvironmentVariable("CHAT_GATEWAY_CONTRACT_TEST") != "true") throw new InvalidOperationException("Test fixture opt-in required.");
var runRoot = Required("CHAT_GATEWAY_CONTRACT_DIRECTORY");
var userKey = Required("CHAT_GATEWAY_CONTRACT_USER_KEY");
var ingressKey = Required("CHAT_GATEWAY_CONTRACT_INGRESS_KEY");
var coreKey = Required("CHAT_GATEWAY_CONTRACT_CORE_KEY");
string? beOrigin = null;
var passed = 0;

// 준비: 실제 CHAT authentication/middleware/STOMP/broker와 BE HTTP 서버 사이를 연결한다.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", Args = [] });
builder.Configuration.Sources.Clear();
builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Chat:Authentication:Enabled"] = "true",
    ["Chat:Authentication:Base64SigningKey"] = userKey,
    ["Chat:ServiceAuthentication:Ingress:Enabled"] = "true",
    ["Chat:ServiceAuthentication:Ingress:Issuer"] = "translacat-be",
    ["Chat:ServiceAuthentication:Ingress:Audience"] = "translacat-chat",
    ["Chat:ServiceAuthentication:Ingress:Service"] = "translacat-be",
    ["Chat:ServiceAuthentication:Ingress:Base64SigningKey"] = ingressKey,
    ["Chat:Identity:Enabled"] = "true",
    ["Chat:Identity:ServiceAuthentication:Issuer"] = "translacat-chat",
    ["Chat:Identity:ServiceAuthentication:Audience"] = "translacat-be",
    ["Chat:Identity:ServiceAuthentication:Service"] = "translacat-chat",
    ["Chat:Identity:ServiceAuthentication:Base64SigningKey"] = coreKey
});
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddChatJwtAuthentication(builder.Configuration);
builder.Services.AddChatServiceAuthentication(builder.Configuration);
builder.Services.PostConfigure<ChatIdentityOptions>(options => options.BaseUrl = beOrigin);
builder.Services.AddChatReadHttp();
builder.Services.AddChatRealtime();
builder.Services.AddSingleton<IChatRealtimeAccess, SyntheticRoomAccess>();
builder.Services.AddSingleton<SyntheticMessageSender>();
builder.Services.AddSingleton<IChatRealtimeMessageSender>(provider => provider.GetRequiredService<SyntheticMessageSender>());
await using var app = builder.Build();
app.UseChatReadHttp();
app.MapChatRealtime();
app.MapPost("/api/v1/chat/contract-probe", async (HttpContext context) =>
{
    using var document = await JsonDocument.ParseAsync(context.Request.Body);
    return Results.Json(new { userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier),
        lastReadMessageId = document.RootElement.GetProperty("lastReadMessageId").GetInt64(), lastReadAt = (string?)null });
}).RequireAuthorization(ChatReadAuthorization.Policy);
await app.StartAsync();
var chatOrigin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
await File.WriteAllTextAsync(Path.Combine(runRoot, "chat.json"), JsonSerializer.Serialize(new { origin = chatOrigin }));
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
while (!File.Exists(Path.Combine(runRoot, "be.json"))) await Task.Delay(100, deadline.Token);
using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runRoot, "be.json"))))
    beOrigin = manifest.RootElement.GetProperty("origin").GetString();
Check(beOrigin is not null && new Uri(beOrigin).Host == "127.0.0.1");

var jwt = UserToken(73);
using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

// 실행 / 검증: BE 현재 사용자 조회와 CHAT의 실제 BE identity callback을 모두 통과한다.
using (var response = await Post(beOrigin!, jwt))
{
    Check(response.StatusCode == HttpStatusCode.OK);
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Check(document.RootElement.GetProperty("userId").GetString() == "73");
    Check(document.RootElement.GetProperty("lastReadMessageId").GetInt64() == long.MaxValue);
    Check(document.RootElement.GetProperty("lastReadAt").ValueKind == JsonValueKind.Null);
    Check(!response.Headers.Contains(ChatServiceIngressGuard.HeaderName));
}
foreach (var scenario in new[] { "missing-user", "wrong-user-id", "forged-service", "direct-chat" })
{
    using var response = await Post(scenario == "direct-chat" ? chatOrigin : beOrigin!,
        scenario == "missing-user" ? null : scenario == "wrong-user-id" ? UserToken(74) : jwt, scenario == "forged-service");
    Check(response.StatusCode == HttpStatusCode.Unauthorized);
}

// 실행 / 검증: 실제 native WS→BE relay→CHAT CONNECT/구독/메시지 전달 및 SEND port 호출이다.
using (var socket = new ClientWebSocket())
{
    socket.Options.AddSubProtocol("v12.stomp");
    await socket.ConnectAsync(new Uri(beOrigin!.Replace("http:", "ws:") + "/ws/chat"), deadline.Token);
    await Send(socket, "CONNECT\naccept-version:1.2\nAuthorization:Bearer " + jwt + "\n\n\0");
    Check((await Receive(socket)).StartsWith("CONNECTED\n", StringComparison.Ordinal));
    Check(socket.SubProtocol == "v12.stomp");
    await Send(socket, "SUBSCRIBE\nid:read\ndestination:/user/queue/chat/read\nreceipt:ready\n\n\0");
    Check((await Receive(socket)).Contains("receipt-id:ready", StringComparison.Ordinal));
    await app.Services.GetRequiredService<ChatRealtimeBroker>().PublishUserAsync("gateway@example.invalid", "/queue/chat/read", "{\"lastReadAt\":null}", deadline.Token);
    var message = await Receive(socket);
    Check(message.StartsWith("MESSAGE\n", StringComparison.Ordinal) && message.Contains("{\"lastReadAt\":null}", StringComparison.Ordinal));
    await Send(socket, "SEND\ndestination:/app/chat/rooms/31/messages\ncontent-type:application/json\nreceipt:sent\n\n{\"content\":\"synthetic-message\"}\0");
    Check((await Receive(socket)).Contains("receipt-id:sent", StringComparison.Ordinal));
    Check(app.Services.GetRequiredService<SyntheticMessageSender>().Calls == 1);
    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", deadline.Token);
}

Console.WriteLine(JsonSerializer.Serialize(new { probe = "actual-be-gateway-to-chat-http-stomp", passed, failed = 0,
    accountStore = "synthetic", businessEndpoint = "synthetic", roomAccessAndMessageStore = "synthetic", transportAndIdentity = "actual" }));
await app.StopAsync();

void Check(bool result)
{
    if (!result) throw new InvalidOperationException("Gateway contract assertion failed.");
    passed++;
}

string UserToken(long id) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
{
    Claims = new Dictionary<string, object> { ["id"] = id, ["sub"] = "gateway@example.invalid" },
    IssuedAt = DateTime.UtcNow, Expires = DateTime.UtcNow.AddMinutes(2),
    SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(userKey)), SecurityAlgorithms.HmacSha512)
});

async Task<HttpResponseMessage> Post(string origin, string? token, bool forged = false)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, origin + "/api/v1/chat/contract-probe")
    {
        Content = new StringContent("{\"lastReadMessageId\":9223372036854775807}", Encoding.UTF8, "application/json")
    };
    if (token is not null) request.Headers.Authorization = new("Bearer", token);
    if (forged) request.Headers.Add(ChatServiceIngressGuard.HeaderName, "Bearer forged");
    return await client.SendAsync(request, deadline.Token);
}

async Task Send(ClientWebSocket socket, string frame) => await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, deadline.Token);

async Task<string> Receive(ClientWebSocket socket)
{
    using var body = new MemoryStream();
    var bytes = new byte[4096];
    do
    {
        var received = await socket.ReceiveAsync(bytes, deadline.Token);
        if (received.MessageType != WebSocketMessageType.Text) throw new InvalidOperationException("Unexpected gateway protocol close.");
        body.Write(bytes, 0, received.Count);
        if (body.Length > 65536) throw new InvalidOperationException("Gateway fixture frame exceeded limit.");
        if (received.EndOfMessage) break;
    } while (true);
    return Encoding.UTF8.GetString(body.ToArray());
}

static string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException("Fixture setting missing.");

sealed class SyntheticRoomAccess : IChatRealtimeAccess
{
    public Task ValidateRoomAccessAsync(long userId, long roomId, CancellationToken cancellationToken)
        => userId == 73 && roomId == 31 ? Task.CompletedTask : Task.FromException(new InvalidOperationException("Synthetic room denied."));
}

sealed class SyntheticMessageSender : IChatRealtimeMessageSender
{
    public int Calls { get; private set; }
    public Task SendTextAsync(long userId, long roomId, string content, CancellationToken cancellationToken)
    {
        if (userId != 73 || roomId != 31 || content != "synthetic-message") throw new InvalidOperationException("Synthetic message mismatch.");
        Calls++;
        return Task.CompletedTask;
    }
}
