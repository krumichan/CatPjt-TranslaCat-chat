using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Api.NotificationPreferences;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.NotificationPreferences;

namespace TranslaCat.Chat.ApiTests.NotificationPreferences;

public sealed class ChatNotificationPreferenceHttpTests
{
    [Theory]
    [InlineData("GET", "/api/v1/chat/notifications/preferences")]
    [InlineData("PUT", "/api/v1/chat/notifications/preferences")]
    [InlineData("GET", "/api/v1/chat/rooms/41/notification-preferences")]
    [InlineData("PUT", "/api/v1/chat/rooms/41/notification-preferences")]
    public async Task All_routes_require_authentication_before_store(string method, string path)
    {
        // 준비 / 실행
        await using var host = await PreferenceHost.StartAsync();
        using var response = await host.SendAsync(method, path, "{}", false);

        // 검증
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Store.Calls);
    }

    [Theory]
    [InlineData("/api/v1/chat/notifications/preferences", "pushEnabled", false)]
    [InlineData("/api/v1/chat/rooms/41/notification-preferences", "muted", true)]
    public async Task Settings_use_authenticated_actor_and_exact_own_boolean(string path, string field, bool value)
    {
        // 준비
        await using var host = await PreferenceHost.StartAsync();

        // 실행
        using var put = await host.SendAsync("PUT", path, JsonSerializer.Serialize(new Dictionary<string,bool> { [field] = value }));
        using var get = await host.SendAsync("GET", path);
        var body = (await ReadHttpFixture.ReadJsonAsync(get)).GetProperty("body");

        // 검증
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(value, body.GetProperty(field).GetBoolean());
        Assert.Equal(73, host.Store.Actor);
        Assert.False(body.TryGetProperty("userId", out _));
        if (field == "muted") Assert.Equal(41, body.GetProperty("roomId").GetInt64());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"pushEnabled\":\"false\"}")]
    [InlineData("{\"pushEnabled\":0}")]
    [InlineData("{\"pushEnabled\":false,\"userId\":99}")]
    [InlineData("{\"pushEnabled\":true,\"pushEnabled\":false}")]
    [InlineData("{")]
    public async Task Invalid_or_ambiguous_json_is_rejected_without_mutation(string body)
    {
        // 준비 / 실행
        await using var host = await PreferenceHost.StartAsync();
        using var response = await host.SendAsync("PUT", "/api/v1/chat/notifications/preferences", body);
        var envelope = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("CHAT_NOTIFICATION_PREFERENCE_INVALID_REQUEST", envelope.GetProperty("body").GetProperty("errorCode").GetString());
        Assert.Equal(0, host.Store.Calls);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task Foreign_inactive_or_missing_room_has_same_scoped_rejection(string method)
    {
        // 준비
        await using var host = await PreferenceHost.StartAsync();
        host.Store.Missing = true;

        // 실행
        using var response = await host.SendAsync(method, "/api/v1/chat/rooms/41/notification-preferences", "{\"muted\":true}");

        // 검증
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = (await ReadHttpFixture.ReadJsonAsync(response)).GetProperty("body");
        Assert.Equal("CHAT_NOTIFICATION_PREFERENCE_NOT_ALLOWED", body.GetProperty("errorCode").GetString());
        Assert.Equal(73, host.Store.Actor);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public async Task Invalid_room_never_reaches_store(string room)
    {
        // 준비 / 실행
        await using var host = await PreferenceHost.StartAsync();
        using var response = await host.SendAsync("GET", $"/api/v1/chat/rooms/{room}/notification-preferences");

        // 검증
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, host.Store.Calls);
    }
}

internal sealed class PreferenceHost(WebApplication application, HttpClient client, PreferenceMemoryStore store) : IAsyncDisposable
{
    public PreferenceMemoryStore Store { get; } = store;
    public static async Task<PreferenceHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddChatReadHttp();
        builder.Services.AddChatNotificationPreferenceHttp();
        builder.Services.AddSingleton(new ChatReadLocalTime(TimeZoneInfo.Utc));
        builder.Services.AddSingleton<TimeProvider>(new FixedReadTimeProvider());
        var store = new PreferenceMemoryStore();
        builder.Services.AddSingleton<IChatNotificationPreferenceStore>(store);
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

internal sealed class PreferenceMemoryStore : IChatNotificationPreferenceStore
{
    public int Calls { get; private set; }
    public long Actor { get; private set; }
    public bool Missing { get; set; }
    private bool enabled = true;
    private bool muted;
    public Task<ChatNotificationPreference> GetAsync(long userId, CancellationToken token)
    { Calls++; Actor = userId; return Task.FromResult(new ChatNotificationPreference(enabled)); }
    public Task<ChatNotificationPreference> PutAsync(long userId, bool pushEnabled, CancellationToken token)
    { Calls++; Actor = userId; enabled = pushEnabled; return Task.FromResult(new ChatNotificationPreference(enabled)); }
    public Task<ChatRoomNotificationPreference?> GetRoomAsync(long userId, long roomId, CancellationToken token)
    { Calls++; Actor = userId; return Task.FromResult(Missing ? null : new ChatRoomNotificationPreference(roomId, muted)); }
    public Task<ChatRoomNotificationPreference?> PutRoomAsync(long userId, long roomId, bool value, CancellationToken token)
    { Calls++; Actor = userId; muted = value; return Task.FromResult(Missing ? null : new ChatRoomNotificationPreference(roomId, muted)); }
}
