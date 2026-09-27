using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.Read;

namespace TranslaCat.Chat.ApiTests;

internal sealed class ReadHttpFixture : IAsyncDisposable
{
    private readonly WebApplication application;

    private ReadHttpFixture(WebApplication application, HttpClient client, HttpReadTransaction transaction)
    {
        this.application = application;
        Client = client;
        Transaction = transaction;
    }

    public HttpClient Client
    {
        get;
    }

    public HttpReadTransaction Transaction
    {
        get;
    }

    public static async Task<ReadHttpFixture> StartAsync(
        bool registerTransaction = true,
        bool registerAuthentication = true,
        TimeProvider? timeProvider = null)
    {
        // 실제 Kestrel과 운영 HTTP 구성을 사용하고 외부 경계만 테스트에서 교체한다.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing",
            ApplicationName = typeof(ChatReadContractMapper).Assembly.FullName
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddChatReadHttp();
        builder.Services.AddSingleton(timeProvider ?? new FixedReadTimeProvider());
        builder.Services.AddSingleton(new ChatReadLocalTime(TimeZoneInfo.Utc));
        builder.Services.AddSingleton(new ChatReadContractMapper(TimeZoneInfo.Utc));

        var transaction = new HttpReadTransaction();
        if (registerTransaction)
        {
            builder.Services.AddSingleton<IChatReadTransaction>(transaction);
        }

        if (registerAuthentication)
        {
            builder.Services.AddAuthentication(TestReadAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestReadAuthenticationHandler>(
                    TestReadAuthenticationHandler.SchemeName, _ => { });
        }

        // ephemeral loopback 주소만 열고 테스트가 끝나면 host를 함께 종료한다.
        var application = builder.Build();
        application.UseChatReadHttp();
        await application.StartAsync();
        var server = application.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };

        return new ReadHttpFixture(application, client, transaction);
    }

    public async Task<HttpResponseMessage> PatchAsync(
        string body,
        string roomId = "41",
        string? identity = "73",
        string contentType = "application/json",
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/chat/rooms/{roomId}/read")
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        };
        if (identity is not null)
        {
            request.Headers.Add(TestReadAuthenticationHandler.HeaderName, identity);
        }

        return await Client.SendAsync(request, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await application.StopAsync();
        await application.DisposeAsync();
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}

internal sealed class FixedReadTimeProvider : TimeProvider
{
    public static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 34, 56, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        return Now;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

// 헤더 기반 인증은 이 테스트 assembly에만 존재한다. JWT 검증 대역이라는 한계를 유지한다.
internal sealed class TestReadAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ReadTestOnly";
    public const string HeaderName = "X-Read-Test-Identity";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = header == "missing-id"
            ? Array.Empty<Claim>()
            : new[] { new Claim(ClaimTypes.NameIdentifier, header.ToString()) };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
