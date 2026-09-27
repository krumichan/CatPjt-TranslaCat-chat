using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Api.Notifications;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.Notifications;

namespace TranslaCat.Chat.ApiTests.Notifications;

public sealed class ChatNotificationHttpTests
{
    [Theory]
    [InlineData("summary", "GET")]
    [InlineData("chats", "GET")]
    [InlineData("activities", "GET")]
    [InlineData("activities/101/read", "PATCH")]
    [InlineData("activities/read-all", "PATCH")]
    public async Task All_public_notification_routes_run_actual_HTTP_pipeline(string suffix, string method)
    {
        // 준비
        await using var host = await NotificationHost.StartAsync();

        // 실행
        using var response = await host.SendAsync(method, suffix);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(200, json.GetProperty("resultCode").GetInt32());
        if (suffix == "summary")
        {
            Assert.Equal(6, json.GetProperty("body").GetProperty("totalAttentionCount").GetInt64());
        }
        if (suffix == "activities")
        {
            var item = Assert.Single(json.GetProperty("body").GetProperty("items").EnumerateArray());
            Assert.False(item.GetProperty("isRead").GetBoolean());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, item.GetProperty("readAt").ValueKind);
            Assert.Equal("2026-09-26T12:34:56Z", item.GetProperty("createdAt").GetString());
            Assert.Equal("synthetic", item.GetProperty("payload").GetProperty("roomName").GetString());
        }
    }

    [Theory]
    [InlineData("activities?onlyUnread=yes&size=999", HttpStatusCode.OK, "")]
    [InlineData("activities?onlyUnread=1", HttpStatusCode.OK, "")]
    [InlineData("activities?onlyUnread=maybe", HttpStatusCode.InternalServerError, "")]
    [InlineData("activities?cursorId=0", HttpStatusCode.BadRequest, "CHAT_NOTIFICATION_CURSOR_INVALID")]
    [InlineData("activities?size=0", HttpStatusCode.BadRequest, "")]
    [InlineData("chats?cursorMessageId=-1", HttpStatusCode.BadRequest, "")]
    [InlineData("activities?cursorId=9223372036854775808", HttpStatusCode.InternalServerError, "")]
    public async Task Query_boolean_size_clamp_and_error_boundaries_match_source(string query, HttpStatusCode status, string code)
    {
        // 준비
        await using var host = await NotificationHost.StartAsync();

        // 실행
        using var response = await host.SendAsync("GET", query);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(status, response.StatusCode);
        if (status != HttpStatusCode.OK)
        {
            Assert.Equal(code, json.GetProperty("body").GetProperty("errorCode").GetString());
        }
        else
        {
            Assert.True(host.Store.OnlyUnread);
            Assert.Equal(query.Contains("999", StringComparison.Ordinal) ? 51 : 21, host.Store.Limit);
        }
    }

    [Fact]
    public async Task Missing_notification_profile_port_does_not_invent_user_display()
    {
        // 준비
        await using var host = await NotificationHost.StartAsync();
        host.Store.ChatRows = [new(41, "DIRECT", "MANUAL", "room", 101, 74, null, null, "TEXT", "synthetic",
            DateTime.SpecifyKind(FixedReadTimeProvider.Now.DateTime, DateTimeKind.Unspecified), 74, 1, 101)];

        // 실행
        using var response = await host.SendAsync("GET", "chats");
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("CHAT_NOTIFICATION_UNAVAILABLE", json.GetProperty("body").GetProperty("errorCode").GetString());
    }
}

internal sealed class NotificationHost(WebApplication application, HttpClient client, NotificationMemoryStore store) : IAsyncDisposable
{
    public NotificationMemoryStore Store { get; } = store;

    public static async Task<NotificationHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddChatReadHttp();
        builder.Services.AddChatNotificationHttp();
        builder.Services.AddSingleton(new ChatReadLocalTime(TimeZoneInfo.Utc));
        builder.Services.AddSingleton<TimeProvider>(new FixedReadTimeProvider());
        var store = new NotificationMemoryStore();
        builder.Services.AddSingleton<IChatNotificationStore>(store);
        builder.Services.AddAuthentication(TestReadAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestReadAuthenticationHandler>(TestReadAuthenticationHandler.SchemeName, _ => { });
        var app = builder.Build();
        app.UseChatReadHttp();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(app, new HttpClient { BaseAddress = new Uri(address) }, store);
    }

    public async Task<HttpResponseMessage> SendAsync(string method, string suffix)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/v1/chat/notifications/" + suffix);
        request.Headers.Add(TestReadAuthenticationHandler.HeaderName, "73");
        return await client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
    }
}

internal sealed class NotificationMemoryStore : IChatNotificationStore
{
    public bool OnlyUnread
    {
        get; private set;
    }
    public int Limit
    {
        get; private set;
    }
    public IReadOnlyList<ChatNotificationChatRow> ChatRows { get; set; } = [];
    private static ChatNotificationActivity Activity => new(101, "CHAT_INVITATION", 41, "{\"roomName\":\"synthetic\"}", false, null,
        DateTime.SpecifyKind(FixedReadTimeProvider.Now.DateTime, DateTimeKind.Unspecified));

    public Task<ChatNotificationSummary> GetSummaryAsync(long userId, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ChatNotificationSummary(4, 2, 2));
    }

    public Task<IReadOnlyList<ChatNotificationActivity>> GetActivitiesAsync(long userId, bool onlyUnread, long? cursorId, int limit, CancellationToken cancellationToken)
    {
        OnlyUnread = onlyUnread;
        Limit = limit;
        return Task.FromResult<IReadOnlyList<ChatNotificationActivity>>([Activity]);
    }
    public Task<IReadOnlyList<ChatNotificationChatRow>> GetUnreadChatsAsync(long userId, long? cursorMessageId, int limit, CancellationToken cancellationToken)
    {
        return Task.FromResult(ChatRows);
    }

    public Task<ChatNotificationActivity?> MarkReadAsync(long userId, long notificationId, DateTime at, CancellationToken cancellationToken)
    {
        return Task.FromResult<ChatNotificationActivity?>(Activity with
        {
            IsRead = true,
            ReadAt = at
        });
    }

    public Task<long> MarkAllReadAsync(long userId, DateTime at, CancellationToken cancellationToken)
    {
        return Task.FromResult(2L);
    }
}
