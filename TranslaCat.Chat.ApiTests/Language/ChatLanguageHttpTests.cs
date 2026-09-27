using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Api.Language;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.Language;

namespace TranslaCat.Chat.ApiTests.Language;

public sealed class ChatLanguageHttpTests
{
    [Theory]
    [InlineData("GET", "/api/v1/users/me/chat-language-settings", null, "SYSTEM")]
    [InlineData("PATCH", "/api/v1/users/me/chat-language-settings", "{}", "DEFAULT")]
    [InlineData("GET", "/api/v1/chat/rooms/41/language-settings", null, "SYSTEM")]
    [InlineData("PATCH", "/api/v1/chat/rooms/41/language-settings", "{}", "ROOM_OVERRIDE")]
    [InlineData("DELETE", "/api/v1/chat/rooms/41/language-settings", null, "SYSTEM")]
    public async Task Public_language_routes_use_real_binding_service_and_flat_contract(string method, string path, string? body, string source)
    {
        // 준비
        await using var host = await LanguageHttpHost.StartAsync();

        // 실행
        using var response = await host.SendAsync(method, path, body);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = json.GetProperty("body");
        Assert.Equal(source, value.GetProperty("source").GetString());
        Assert.Equal(73, value.GetProperty("userId").GetInt64());
        Assert.Equal("ko", value.GetProperty("originalLanguageCode").GetString());
        Assert.Equal("ja", value.GetProperty("translationLanguageCode").GetString());
        Assert.True(value.GetProperty("showOriginal").GetBoolean());
        Assert.False(value.TryGetProperty("values", out _));
    }

    [Theory]
    [InlineData("{\"originalLanguageCode\":3,\"showOriginal\":0,\"showTranslation\":\"TRUE\"}", HttpStatusCode.OK)]
    [InlineData("{\"showOriginal\":\"tRuE\"}", HttpStatusCode.InternalServerError)]
    [InlineData("{\"showOriginal\":1.0}", HttpStatusCode.InternalServerError)]
    [InlineData("{\"showOriginal\":[]}", HttpStatusCode.InternalServerError)]
    [InlineData("null", HttpStatusCode.InternalServerError)]
    [InlineData("{broken", HttpStatusCode.InternalServerError)]
    public async Task Jackson_scalar_and_invalid_body_boundaries_are_preserved(string body, HttpStatusCode expected)
    {
        // 준비
        await using var host = await LanguageHttpHost.StartAsync();

        // 실행
        using var response = await host.SendAsync("PATCH", "/api/v1/users/me/chat-language-settings", body);

        // 검증
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            var json = await ReadHttpFixture.ReadJsonAsync(response);
            Assert.Equal("3", json.GetProperty("body").GetProperty("originalLanguageCode").GetString());
            Assert.False(json.GetProperty("body").GetProperty("showOriginal").GetBoolean());
        }
    }

    [Fact]
    public async Task Other_room_membership_returns_business_400()
    {
        // 준비
        await using var host = await LanguageHttpHost.StartAsync();

        // 실행
        using var response = await host.SendAsync("GET", "/api/v1/chat/rooms/42/language-settings", null);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("CHAT_ROOM_ACCESS_DENIED", json.GetProperty("body").GetProperty("errorCode").GetString());
    }
}

internal sealed class LanguageHttpHost(WebApplication application, HttpClient client) : IAsyncDisposable
{
    public static async Task<LanguageHttpHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddChatReadHttp();
        builder.Services.AddChatLanguageHttp();
        builder.Services.AddSingleton<IChatLanguageStore, MemoryLanguageStore>();
        builder.Services.AddAuthentication(TestReadAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestReadAuthenticationHandler>(TestReadAuthenticationHandler.SchemeName, _ => { });
        var app = builder.Build();
        app.UseChatReadHttp();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(app, new HttpClient { BaseAddress = new Uri(address) });
    }

    public async Task<HttpResponseMessage> SendAsync(string method, string path, string? body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(TestReadAuthenticationHandler.HeaderName, "73");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        return await client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
    }

    private sealed class MemoryLanguageStore : IChatLanguageStore, IChatLanguageSession
    {
        private ChatLanguageValues? defaults;
        private ChatRoomLanguageState room = new(7, null, null, true, true);
        public Task<T> ExecuteAsync<T>(bool write, Func<IChatLanguageSession, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        {
            return work(this, cancellationToken);
        }

        public Task<ChatLanguageValues?> GetDefaultAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult(defaults);
        }

        public Task<ChatRoomLanguageState> GetRoomAsync(long userId, long roomId, CancellationToken cancellationToken)
        {
            return roomId == 41 ? Task.FromResult(room) : throw new ChatLanguageException("채팅방 접근 권한이 없습니다.", "CHAT_ROOM_ACCESS_DENIED");
        }

        public Task SaveDefaultAsync(long userId, ChatLanguageValues value, CancellationToken cancellationToken)
        {
            defaults = value;
            return Task.CompletedTask;
        }
        public Task SaveRoomAsync(long userId, long memberId, ChatLanguageValues? value, CancellationToken cancellationToken)
        {
            room = new(memberId, value?.OriginalLanguageCode, value?.TranslationLanguageCode, value?.ShowOriginal ?? true, value?.ShowTranslation ?? true);
            return Task.CompletedTask;
        }
    }
}
