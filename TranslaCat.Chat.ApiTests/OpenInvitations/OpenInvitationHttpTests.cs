using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Api.Notifications;
using TranslaCat.Chat.Api.OpenInvitations;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Application.OpenInvitations;

namespace TranslaCat.Chat.ApiTests.OpenInvitations;

public sealed class OpenInvitationHttpTests
{
    [Theory]
    [InlineData("GET", "/api/v1/chat/open-invitations")]
    [InlineData("GET", "/api/v1/chat/open-invitations/summary")]
    [InlineData("GET", "/api/v1/chat/open-invitations/101")]
    [InlineData("PATCH", "/api/v1/chat/open-invitations/101/read")]
    [InlineData("POST", "/api/v1/chat/open-rooms/41/invitations")]
    public async Task Routes_require_authentication(string method, string path)
    {
        // 준비 / 실행
        await using var host = await InvitationHost.StartAsync();
        using var response = await host.SendAsync(method, path, authenticated: false);

        // 검증
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Store.Calls);
    }

    [Fact]
    public async Task Create_uses_authenticated_actor_and_returns_only_public_receipt()
    {
        // 준비
        await using var host = await InvitationHost.StartAsync();
        var requestId = Guid.NewGuid().ToString("D");

        // 실행
        using var response = await host.SendAsync("POST", "/api/v1/chat/open-rooms/41/invitations",
            JsonSerializer.Serialize(new { targetPublicId = "  TC-TARGET  ", requestId }));
        var json = await ReadHttpFixture.ReadJsonAsync(response);
        var body = json.GetProperty("body");

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(73, host.Store.Actor);
        Assert.Equal(41, host.Store.Room);
        Assert.Equal("TC-TARGET", host.Store.Request!.TargetPublicId);
        Assert.Equal(Guid.Parse(requestId), host.Store.Request.RequestId);
        Assert.Equal(101, body.GetProperty("invitationId").GetInt64());
        Assert.Equal(41, body.GetProperty("roomId").GetInt64());
        Assert.Equal("2026-01-01T00:00:00Z", body.GetProperty("createdAt").GetString());
        Assert.False(body.GetProperty("duplicate").GetBoolean());
        Assert.Equal(4, body.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"targetPublicId\":\"TC-TARGET\",\"requestId\":\"invalid\"}")]
    [InlineData("{\"targetPublicId\":5,\"requestId\":null}")]
    [InlineData("{")]
    public async Task Invalid_create_is_business_400_without_store_call(string body)
    {
        // 준비 / 실행
        await using var host = await InvitationHost.StartAsync();
        using var response = await host.SendAsync("POST", "/api/v1/chat/open-rooms/41/invitations", body);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("OPEN_INVITATION_INVALID_REQUEST", json.GetProperty("body").GetProperty("errorCode").GetString());
        Assert.Equal(0, host.Store.Calls);
    }

    [Fact]
    public async Task List_summary_detail_and_read_return_existing_activity_object_contract()
    {
        // 준비
        await using var host = await InvitationHost.StartAsync();

        // 실행 / 검증
        foreach (var (method, path) in new[] { ("GET", "?size=999&cursorId=200"), ("GET", "/summary"),
                     ("GET", "/101"), ("PATCH", "/101/read") })
        {
            using var response = await host.SendAsync(method, "/api/v1/chat/open-invitations" + path);
            var body = (await ReadHttpFixture.ReadJsonAsync(response)).GetProperty("body");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var item = path.StartsWith('?') ? body.GetProperty("items")[0]
                : path == "/summary" ? body.GetProperty("latestInvitation") : body;
            Assert.Equal("OPEN_LINK", item.GetProperty("payload").GetProperty("invitationKind").GetString());
            Assert.False(item.TryGetProperty("payloadJson", out _));
            Assert.Equal(73, host.Store.Actor);
        }
        Assert.Equal(51, host.Store.Limit);
        Assert.Equal(200, host.Store.Cursor);
    }

    [Theory]
    [InlineData("GET", "/101")]
    [InlineData("PATCH", "/101/read")]
    public async Task Missing_or_foreign_item_returns_scoped_not_found(string method, string path)
    {
        // 준비
        await using var host = await InvitationHost.StartAsync();
        host.Store.Missing = true;

        // 실행 / 검증
        using var response = await host.SendAsync(method, "/api/v1/chat/open-invitations" + path);
        var body = (await ReadHttpFixture.ReadJsonAsync(response)).GetProperty("body");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("OPEN_INVITATION_NOT_FOUND", body.GetProperty("errorCode").GetString());
    }
}

internal sealed class InvitationHost(WebApplication application, HttpClient client, InvitationMemoryStore store) : IAsyncDisposable
{
    public InvitationMemoryStore Store { get; } = store;
    public static async Task<InvitationHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddChatReadHttp();
        builder.Services.AddChatNotificationHttp();
        builder.Services.AddOpenInvitationHttp();
        builder.Services.AddSingleton(new ChatReadLocalTime(TimeZoneInfo.Utc));
        builder.Services.AddSingleton<TimeProvider>(new FixedReadTimeProvider());
        var store = new InvitationMemoryStore();
        builder.Services.AddSingleton<IOpenInvitationStore>(store);
        builder.Services.AddAuthentication(TestReadAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestReadAuthenticationHandler>(TestReadAuthenticationHandler.SchemeName, _ => { });
        var app = builder.Build();
        app.UseChatReadHttp();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(app, new HttpClient { BaseAddress = new Uri(address) }, store);
    }
    public async Task<HttpResponseMessage> SendAsync(string method, string path, string? body = null, bool authenticated = true)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (authenticated) request.Headers.Add(TestReadAuthenticationHandler.HeaderName, "73");
        return await client.SendAsync(request);
    }
    public async ValueTask DisposeAsync() { client.Dispose(); await application.StopAsync(); await application.DisposeAsync(); }
}

internal sealed class InvitationMemoryStore : IOpenInvitationStore
{
    public int Calls { get; private set; }
    public long Actor { get; private set; }
    public long Room { get; private set; }
    public int Limit { get; private set; }
    public long? Cursor { get; private set; }
    public bool Missing { get; set; }
    public OpenInvitationValidatedCreate? Request { get; private set; }
    private static ChatNotificationActivity Item => new(101, "CHAT_INVITATION", 41,
        "{\"schemaVersion\":1,\"invitationKind\":\"OPEN_LINK\",\"roomType\":\"OPEN\",\"roomName\":null,\"inviterOpenNickname\":\"방의 이름\",\"inviterMemberCode\":\"OC-TEST\"}", false, null, new(2026, 1, 1));
    public Task<OpenInvitationCreated> CreateAsync(long userId, long roomId, OpenInvitationValidatedCreate request, CancellationToken token)
    { Calls++; Actor = userId; Room = roomId; Request = request; return Task.FromResult(new OpenInvitationCreated(101, roomId, Item.CreatedAt, false)); }
    public Task<IReadOnlyList<ChatNotificationActivity>> ListAsync(long userId, long? cursorId, int limit, CancellationToken token)
    { Calls++; Actor = userId; Limit = limit; Cursor = cursorId; return Task.FromResult<IReadOnlyList<ChatNotificationActivity>>([Item]); }
    public Task<OpenInvitationSummary> SummaryAsync(long userId, CancellationToken token)
    { Calls++; Actor = userId; return Task.FromResult(new OpenInvitationSummary(1, Item)); }
    public Task<ChatNotificationActivity?> GetAsync(long userId, long invitationId, CancellationToken token)
    { Calls++; Actor = userId; return Task.FromResult(Missing ? null : (ChatNotificationActivity?)Item); }
    public Task<ChatNotificationActivity?> ReadAsync(long userId, long invitationId, DateTime now, CancellationToken token)
    { Calls++; Actor = userId; return Task.FromResult(Missing ? null : (ChatNotificationActivity?)(Item with { IsRead = true, ReadAt = now })); }
}
