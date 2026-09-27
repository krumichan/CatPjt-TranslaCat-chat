using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Api.Realtime;

namespace TranslaCat.Chat.ApiTests.Configuration;

public sealed class ChatRealtimeConfigurationTests
{
    [Theory]
    [InlineData("https://client.example.invalid", "https://client.example.invalid", 400)]
    [InlineData("https://client.example.invalid", "https://attacker.example.invalid", 403)]
    [InlineData(null, "https://client.example.invalid", 403)]
    [InlineData(null, null, 400)]
    public async Task Origin_is_checked_before_WebSocket_upgrade(string? configured, string? supplied, int expected)
    {
        // 준비: endpoint의 실제 routing/origin 분기만 검증한다. JWT/업무 연결은 별도 인증 테스트 범위다.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddChatRealtime();
        builder.Services.AddSingleton(new ChatRealtimeOptions
        {
            AllowedOrigins = configured is null ? [] : [configured]
        });
        await using var app = builder.Build();
        app.MapChatRealtime();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ws/chat");
        if (supplied is not null)
        {
            request.Headers.Add("Origin", supplied);
        }

        // 실행
        using var response = await client.SendAsync(request);

        // 검증: 허용 origin은 다음 upgrade 검사(일반 HTTP이므로400), 나머지는 먼저403이다.
        Assert.Equal((HttpStatusCode)expected, response.StatusCode);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("null")]
    [InlineData("https://client.example.invalid/path")]
    [InlineData("https://user:password@client.example.invalid")]
    [InlineData("https://client.example.invalid?key=private")]
    public void Origin_configuration_requires_an_exact_origin_without_credentials_or_path(string origin)
    {
        // 준비
        var options = new ChatRealtimeOptions { AllowedOrigins = [origin] };

        // 실행
        var error = Assert.Throws<InvalidOperationException>(() => options.Validate());

        // 검증
        Assert.DoesNotContain(origin, error.Message);
    }
}
