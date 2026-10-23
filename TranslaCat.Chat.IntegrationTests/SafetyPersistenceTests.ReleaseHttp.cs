using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Safety;
using TranslaCat.Chat.Api.ServiceAuthentication;
using TranslaCat.Chat.Application.Safety;
using TranslaCat.Chat.Infrastructure.Persistence;

namespace TranslaCat.Chat.IntegrationTests;

public sealed partial class SafetyPersistenceTests
{
    [Theory]
    [InlineData("complete", 200)]
    [InlineData("disabled", 503)]
    [InlineData("missing-column", 503)]
    public async Task Release_ready_HTTP_uses_target_bound_service_token_actual_cleanup_and_complete_schema(string scenario, int expected)
    {
        // 준비: 외부 provider만 합성이고 인증/resource filter/controller/DI/삭제 store/DB는 실제 경로다.
        var seed = await SeedAsync(67001, "OPEN");
        await using var host = await DeletionHttpHost.StartAsync(fixture.Contexts, scenario != "disabled");
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        bool altered = scenario == "missing-column";
        if (altered) await db.Database.ExecuteSqlRawAsync("ALTER TABLE chat_open_profile_upload_intent RENAME COLUMN attempts TO synthetic_http_missing_attempts");
        try
        {
            // 실행: 토큰 subject/path를 일치시키고 capability 성공 뒤 같은 operation으로 두 번 접수한다.
            string path = $"/internal/v1/chat/accounts/{seed.UserId}/deletion";
            using var capability = new HttpRequestMessage(HttpMethod.Get, path + "/capabilities");
            capability.Headers.Authorization = new("Bearer", host.Token(seed.UserId));
            using var ready = await host.Client.SendAsync(capability);
            Assert.Equal((HttpStatusCode)expected, ready.StatusCode);
            Assert.Equal("no-store", ready.Headers.CacheControl?.ToString());
            if (expected == 200)
            {
                using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
                Assert.Equal("chat-account-deletion-v1", body.RootElement.GetProperty("version").GetString());
                Assert.True(body.RootElement.GetProperty("ready").GetBoolean());
            }
            var operation = new ChatAccountDeletionRequest(Guid.NewGuid().ToString("D"), DateTimeOffset.UtcNow, "synthetic-http@example.invalid");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(operation) };
                request.Headers.Authorization = new("Bearer", host.Token(seed.UserId));
                using var result = await host.Client.SendAsync(request);
                Assert.Equal((HttpStatusCode)expected, result.StatusCode);
                string text = await result.Content.ReadAsStringAsync();
                Assert.DoesNotContain("synthetic-http@example.invalid", text);
                if (expected == 200)
                {
                    using var body = JsonDocument.Parse(text);
                    Assert.Equal("COMPLETED", body.RootElement.GetProperty("status").GetString());
                    Assert.Equal(operation.OperationId, body.RootElement.GetProperty("operationId").GetString());
                }
            }

            // 검증: disabled/불완전 schema는 실제 tombstone도 남기지 않는다.
            Assert.Equal(expected == 200, await db.ChatAccountDeletions.AnyAsync(row => row.UserId == seed.UserId));
            Assert.Equal(expected != 200, await db.ChatRoomMembers.AnyAsync(row => row.UserId == seed.UserId && row.Active));
        }
        finally
        {
            if (altered) await db.Database.ExecuteSqlRawAsync("ALTER TABLE chat_open_profile_upload_intent RENAME COLUMN synthetic_http_missing_attempts TO attempts");
        }
    }

    private sealed class DeletionHttpHost(WebApplication app, HttpClient client, byte[] key) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public string Token(long userId) => ChatServiceJwt.Issue(new()
        {
            Issuer = "translacat-be", Audience = "translacat-chat", Service = "translacat-be",
            Base64SigningKey = Convert.ToBase64String(key)
        }, "Testing", "chat-account-delete", "chat:account-delete", userId, DateTimeOffset.UtcNow);

        public static async Task<DeletionHttpHost> StartAsync(IDbContextFactory<ChatDbContext> contexts, bool enabled)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            byte[] key = RandomNumberGenerator.GetBytes(32);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Chat:SourceTimeZone"] = "UTC", ["Chat:Safety:Enabled"] = enabled.ToString(),
                ["Chat:Safety:AccountDeletionEnabled"] = enabled.ToString(),
                [ChatServiceIngressOptions.SectionName + ":Enabled"] = "true",
                [ChatServiceIngressOptions.SectionName + ":Issuer"] = "translacat-be",
                [ChatServiceIngressOptions.SectionName + ":Audience"] = "translacat-chat",
                [ChatServiceIngressOptions.SectionName + ":Service"] = "translacat-be",
                [ChatServiceIngressOptions.SectionName + ":Base64SigningKey"] = Convert.ToBase64String(key)
            });
            builder.Services.AddChatReadHttp();
            builder.Services.AddChatServiceAuthentication(builder.Configuration);
            builder.Services.AddSingleton(contexts);
            builder.Services.AddSingleton(JournalCore(new JournalDeleteHandler()));
            builder.Services.AddChatSafety(builder.Configuration);
            // 이 fixture는 요청 자체를 검증한다. background scanner와 동일 합성 catalog를 경쟁시키지 않는다.
            builder.Services.RemoveAll<IHostedService>();
            var application = builder.Build();
            application.UseChatReadHttp();
            await application.StartAsync();
            string address = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(application, new HttpClient { BaseAddress = new Uri(address) }, key);
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); }
    }
}
