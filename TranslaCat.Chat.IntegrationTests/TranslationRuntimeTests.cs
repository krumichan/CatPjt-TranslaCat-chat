using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Api.Translation;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Translation;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class TranslationRuntimeTests(ChatMySqlFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http_manual_retry_runs_worker_local_ai_mysql_commit_and_cross_app_stomp(bool fail)
    {
        // 준비 — 실제 Provider 대신 loopback HTTP AI stub을 사용하며 나머지는 실제 adapter다.
        await using var ai = await TranslationAiStub.StartAsync(fail);
        var seeded = await SeedAsync("FAILED");
        var prefix = Prefix();
        await using var writer = await ChatRuntimeTestHost.StartAsync(fixture, prefix, configure: services =>
        {
            services.AddSingleton<IChatMessageProfileReader, SyntheticProfiles>();
            services.AddChatTranslation(new ChatTranslationOptions
            {
                Enabled = true,
                AiBaseUri = ai.Address,
                ApiKey = "synthetic-translation-key",
                AttemptTimeout = TimeSpan.FromSeconds(5),
                ConnectTimeout = TimeSpan.FromSeconds(1),
                MaximumAttempts = 1,
                RetryWait = TimeSpan.Zero,
                InitialDelay = TimeSpan.FromHours(1)
            });
        });
        await using var receiver = await ChatRuntimeTestHost.StartAsync(fixture, prefix);
        using var socket = await receiver.ConnectAsync(seeded.Room.UserId);
        await socket.SubscribeAsync("translation", $"/topic/chat/rooms/{seeded.Room.RoomId}");

        // 실행 — HTTP200 응답은 PENDING commit을 뜻하며 번역 결과는 이후 worker가 따로 commit한다.
        using var request = RetryRequest(writer, seeded.Room.UserId, seeded.Room.RoomId, seeded.Room.FirstId, "JA");
        using var response = await writer.Client.SendAsync(request);
        using var responseBody = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var eventType = fail ? "chat.translation.failed" : "chat.translation.completed";
        var received = await socket.ReceiveEventAsync(eventType);

        // 검증 — 다른 app의 WebSocket 수신 후 독립 DB connection에서 결과 commit을 확인한다.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("PENDING", responseBody.RootElement.GetProperty("body").GetProperty("status").GetString());
        Assert.Equal(seeded.Id, received.GetProperty("translationId").GetInt64());
        Assert.Equal("ja", received.GetProperty("languageCode").GetString());
        Assert.Equal(1, ai.CallCount);
        Assert.Equal("synthetic-translation-key", ai.ApiKey);
        Assert.Equal("ja", ai.TargetLanguage);
        Assert.Equal("합성 메시지 日本語 😺", ai.Text);
        Assert.EndsWith("Z", received.GetProperty("occurredAt").GetString());

        await using var context = await fixture.Contexts.CreateDbContextAsync();
        var row = await context.ChatMessageTranslations.SingleAsync(value => value.Id == seeded.Id);
        Assert.Equal(fail ? "FAILED" : "COMPLETED", row.Status);
        Assert.Null(row.ProcessingToken);
        if (fail)
        {
            Assert.Equal(JsonValueKind.Null, received.GetProperty("translatedContent").ValueKind);
            Assert.Equal("AI Server Chat Translation Error", received.GetProperty("failureReason").GetString());
            Assert.Null(row.CompletedAt);
        }
        else
        {
            Assert.Equal("合成翻訳", received.GetProperty("translatedContent").GetString());
            Assert.False(received.TryGetProperty("failureReason", out _));
            Assert.Equal("合成翻訳", row.TranslatedContent);
            Assert.NotNull(row.CompletedAt);
        }
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("COMPLETED")]
    public async Task Http_noop_remains_available_when_ai_is_unconfigured_and_denies_nonmember(string status)
    {
        // 준비
        var seeded = await SeedAsync(status);
        await using var host = await ChatRuntimeTestHost.StartAsync(fixture, Prefix(), configure: services =>
            services.AddChatTranslation(new()));

        // 실행
        using var request = RetryRequest(host, seeded.Room.UserId, seeded.Room.RoomId, seeded.Room.FirstId, "ja");
        using var response = await host.Client.SendAsync(request);
        using var deniedRequest = RetryRequest(host, seeded.Room.UserId + 99, seeded.Room.RoomId, seeded.Room.FirstId, "ja");
        using var denied = await host.Client.SendAsync(deniedRequest);

        // 검증
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(status, body.RootElement.GetProperty("body").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Contains("CHAT_ROOM_MEMBER_ACCESS_DENIED", await denied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Http_failed_retry_without_ai_config_fails_closed_and_preserves_failed_row()
    {
        // 준비
        var seeded = await SeedAsync("FAILED");
        await using var host = await ChatRuntimeTestHost.StartAsync(fixture, Prefix(), configure: services =>
            services.AddChatTranslation(new()));

        // 실행
        using var request = RetryRequest(host, seeded.Room.UserId, seeded.Room.RoomId, seeded.Room.FirstId, "ja");
        using var response = await host.Client.SendAsync(request);

        // 검증
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal("FAILED", (await context.ChatMessageTranslations.SingleAsync(row => row.Id == seeded.Id)).Status);
    }

    [Fact]
    public async Task Http_failed_retry_with_missing_redis_registration_never_calls_local_ai_or_changes_state()
    {
        // 준비
        await using var ai = await TranslationAiStub.StartAsync(false);
        var seeded = await SeedAsync("FAILED");
        await using var host = await ChatRuntimeTestHost.StartAsync(fixture, Prefix(), redisConfigured: false,
            configure: services => services.AddChatTranslation(new ChatTranslationOptions
            {
                Enabled = true,
                AiBaseUri = ai.Address,
                ApiKey = "synthetic-only"
            }));

        // 실행
        using var request = RetryRequest(host, seeded.Room.UserId, seeded.Room.RoomId, seeded.Room.FirstId, "ja");
        using var response = await host.Client.SendAsync(request);

        // 검증
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, ai.CallCount);
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal("FAILED", (await context.ChatMessageTranslations.SingleAsync(row => row.Id == seeded.Id)).Status);
    }

    private async Task<(SeededReadRoom Room, long Id)> SeedAsync(string status)
    {
        var room = await fixture.SeedReadRoomAsync();
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        var translation = new ChatMessageTranslationEntity
        {
            ChatMessageId = room.FirstId,
            LanguageCode = "ja",
            Status = status,
            CreatedAt = ChatMySqlFixture.Epoch,
            UpdatedAt = ChatMySqlFixture.Epoch
        };
        context.ChatMessageTranslations.Add(translation);
        await context.SaveChangesAsync();
        return (room, translation.Id);
    }

    private static HttpRequestMessage RetryRequest(ChatRuntimeTestHost host, long userId, long roomId, long messageId, string language)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/chat/rooms/{roomId}/messages/{messageId}/translations/{language}/retry");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.CreateToken(userId));
        return request;
    }

    private static string Prefix()
    {
        return "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID")
        + ":translation:" + Guid.NewGuid().ToString("N");
    }

    private sealed class SyntheticProfiles : IChatMessageProfileReader
    {
        public Task<string> GetAuditIdentityAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult("synthetic-audit");
        }

        public Task<ChatUserMessageProfile> GetUserAsync(long userId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<string?> ResolveObjectUrlAsync(string objectKey, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class TranslationAiStub(WebApplication application, Uri address) : IAsyncDisposable
    {
        public Uri Address { get; } = address;
        public int CallCount
        {
            get; private set;
        }
        public string? ApiKey
        {
            get; private set;
        }
        public string? Text
        {
            get; private set;
        }
        public string? TargetLanguage
        {
            get; private set;
        }

        public static async Task<TranslationAiStub> StartAsync(bool fail)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            TranslationAiStub? stub = null;
            app.MapPost("/internal/v1/model/execute", async (HttpContext context) =>
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body);
                stub!.CallCount++;
                stub.ApiKey = context.Request.Headers["X-API-KEY"];
                string prompt = document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                stub.Text = prompt.Split("<message>\n", 2)[1].Split("\n</message>", 2)[0];
                stub.TargetLanguage = prompt.Split("Target language code: ", 2)[1].Split('\n', 2)[0];
                if (fail)
                {
                    context.Response.StatusCode = 422;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        detail = "synthetic validation failure"
                    });
                    return;
                }

                await context.Response.WriteAsJsonAsync(new
                {
                    output = "  合成翻訳  ",
                    providerCalls = 1
                });
            });
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            stub = new(app, new Uri(address));
            return stub;
        }

        public async ValueTask DisposeAsync()
        {
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }
}
