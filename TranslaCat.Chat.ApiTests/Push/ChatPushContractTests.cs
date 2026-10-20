using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TranslaCat.Chat.Api.Core;
using TranslaCat.Chat.Api.Push;
using TranslaCat.Chat.Api.ServiceAuthentication;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Infrastructure.Core;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Push;

namespace TranslaCat.Chat.ApiTests.Push;

public sealed class ChatPushContractTests
{
    private const string Project = "55b79b33-07b9-4d4b-a3f8-cf24ea76b139";
    private const string Installation = "c2a1b179-bbe5-4be9-a8c9-6206767d4d60";
    private static ChatCorePushTarget Target => new(42, Installation, "synthetic_binding", 7, 11, "ExpoPushToken[synthetic-token]", "android");

    [Fact]
    public async Task Core_push_uses_exact_private_path_scope_raw_body_and_conditional_versions()
    {
        // 준비
        var scopes = new RecordingTokens();
        var requests = new List<(string Path, string Body)>();
        using var http = new HttpClient(new Handler(async request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync()));
            return request.RequestUri.AbsolutePath.EndsWith("/query")
                ? Json(new ChatCorePushTargets([Target])) : new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        var core = new HttpChatCoreClient(http, new(new Uri("https://synthetic.invalid"), TimeSpan.FromSeconds(1)), scopes);

        // 실행
        var result = await core.PushTargetsAsync([42], "development", Project, default);
        await core.InvalidatePushAsync(Installation, Target.AccountBindingId, 7, 11, default);

        // 검증
        Assert.Equal(Target, Assert.Single(result.Targets));
        Assert.Equal(["chat:push-targets:read", "chat:push-installations:invalidate"], scopes.Values);
        Assert.Equal("/internal/v1/chat/push-targets/query", requests[0].Path);
        Assert.Equal("/internal/v1/chat/push-installations/invalidate", requests[1].Path);
        using var query = JsonDocument.Parse(requests[0].Body);
        Assert.Equal(3, query.RootElement.EnumerateObject().Count());
        Assert.Equal(42, query.RootElement.GetProperty("userIds")[0].GetInt64());
        Assert.Equal(Project, query.RootElement.GetProperty("projectId").GetString());
        using var invalidate = JsonDocument.Parse(requests[1].Body);
        Assert.Equal(4, invalidate.RootElement.EnumerateObject().Count());
        Assert.Equal(Installation, invalidate.RootElement.GetProperty("installationId").GetString());
        Assert.Equal(Target.AccountBindingId, invalidate.RootElement.GetProperty("accountBindingId").GetString());
        Assert.Equal(7, invalidate.RootElement.GetProperty("generation").GetInt64());
        Assert.Equal(11, invalidate.RootElement.GetProperty("tokenVersion").GetInt64());
    }

    [Theory]
    [InlineData("{\"body\":{\"targets\":[]}}")]
    [InlineData("{\"targets\":[null]}")]
    [InlineData("{\"targets\":null}")]
    [InlineData("{\"targets\":[],\"targets\":[]}")]
    [InlineData("{\"targets\":[],\"extra\":true}")]
    public async Task Core_push_rejects_wrong_envelope_and_invalid_response(string response)
    {
        // 준비
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(response, Encoding.UTF8, "application/json") })));
        var core = new HttpChatCoreClient(http, new(new Uri("https://synthetic.invalid"), TimeSpan.FromSeconds(1)), new RecordingTokens());

        // 실행
        var error = await Record.ExceptionAsync(() => core.PushTargetsAsync([42], "development", Project, default));

        // 검증
        Assert.IsType<ChatCoreUnavailableException>(error);
        Assert.Null(error!.InnerException);
    }

    [Theory]
    [InlineData(0, "development", Project)]
    [InlineData(42, "staging", Project)]
    [InlineData(42, "development", "wrong-project")]
    public async Task Core_push_rejects_invalid_request_before_HTTP(long id, string environment, string project)
    {
        // 준비
        int calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Task.FromResult(Json(new ChatCorePushTargets([]))); }));
        var core = new HttpChatCoreClient(http, new(new Uri("https://synthetic.invalid"), TimeSpan.FromSeconds(1)), new RecordingTokens());

        // 실행
        await Assert.ThrowsAsync<ChatCoreUnavailableException>(() => core.PushTargetsAsync([id], environment, project, default));

        // 검증
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public async Task Core_push_token_inner_length_matches_registry_contract(int innerLength, bool accepted)
    {
        // 준비: BE는 prefix를 제외한 괄호 내부를 최대 256자로 제한한다.
        var target = Target with { ExpoPushToken = "ExponentPushToken[" + new string('a', innerLength) + "]" };
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(new ChatCorePushTargets([target])))));
        var core = new HttpChatCoreClient(http, new(new Uri("https://synthetic.invalid"), TimeSpan.FromSeconds(1)), new RecordingTokens());

        // 실행 / 검증
        if (accepted) Assert.Equal(target, Assert.Single((await core.PushTargetsAsync([42], "development", Project, default)).Targets));
        else await Assert.ThrowsAsync<ChatCoreUnavailableException>(() => core.PushTargetsAsync([42], "development", Project, default));
    }
    [Fact]
    public void Push_flags_require_explicit_valid_configuration_and_no_worker_by_default()
    {
        // 준비
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();

        // 실행
        services.AddChatPush(config);

        // 검증
        Assert.False(new ChatPushOptions().Enabled);
        Assert.DoesNotContain(services, row => row.ImplementationType == typeof(ChatPushWorker));
        Assert.Throws<InvalidOperationException>(() => new ChatPushOptions { Enabled = true }.Validate());
        new ChatPushOptions { Enabled = true, AppEnvironment = "development", ProjectId = Project }.Validate();
    }

    [Fact]
    public void Service_token_allowlist_includes_two_push_operations_and_rejects_lookalikes()
    {
        // 준비
        var provider = new ChatCoreAccessTokenProvider(new ChatServiceTokenSettings
        {
            Issuer = "synthetic-chat", Audience = "synthetic-be", Service = "synthetic-chat",
            Base64SigningKey = Convert.ToBase64String(new byte[32])
        }, "Testing", TimeProvider.System);

        // 실행 / 검증
        Assert.NotEmpty(provider.CreateToken("chat:push-targets:read"));
        Assert.NotEmpty(provider.CreateToken("chat:push-installations:invalidate"));
        Assert.Throws<ChatCoreUnavailableException>(() => provider.CreateToken("chat:push-targets:write"));
    }

    [Fact]
    public void Payload_preserves_large_ids_as_decimal_strings_and_exact_binding_without_private_content()
    {
        // 준비
        var row = new ChatPushOutboxEntity { RoomId = 9007199254740993, MessageId = long.MaxValue, EventId = "synthetic_event" };

        // 실행
        string json = ExpoChatPushClient.SerializePayload(ChatPushProcessor.Payload(row, Target));
        using var payload = JsonDocument.Parse(json);
        var data = payload.RootElement.GetProperty("data");

        // 검증
        Assert.Equal("9007199254740993", data.GetProperty("roomId").GetString());
        Assert.Equal("9223372036854775807", data.GetProperty("messageId").GetString());
        Assert.Equal(Target.AccountBindingId, data.GetProperty("accountBindingId").GetString());
        Assert.Equal(5, data.EnumerateObject().Count());
        Assert.Equal("TranslaCat", payload.RootElement.GetProperty("title").GetString());
        Assert.Equal("새 채팅 메시지가 있어요.", payload.RootElement.GetProperty("body").GetString());
    }

    [Fact]
    public void Snapshot_and_additive_migration_have_no_untracked_model_change_or_external_foreign_key()
    {
        // 준비: provider에 model만 생성하며 실제 connection은 열지 않는다.
        using var db = new ChatDbContext(new DbContextOptionsBuilder<ChatDbContext>()
            .UseMySQL("Server=127.0.0.1;Database=translacat_chat;User=synthetic;Password=synthetic").Options);

        // 실행
        string script = db.GetService<IMigrator>().GenerateScript("20260926155844_TranslationProcessingLease");

        // 검증
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("CREATE TABLE `chat_push_outbox`", script);
        Assert.Contains("CREATE TABLE `chat_push_delivery`", script);
        Assert.DoesNotContain("ALTER TABLE `chat_message`", script);
        Assert.Contains("CHARACTER SET ascii COLLATE ascii_bin", script);
        Assert.Contains("CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci", script);
        var outbox = db.Model.FindEntityType(typeof(ChatPushOutboxEntity))!;
        Assert.Contains(outbox.GetIndexes(), index => index.IsUnique && index.Properties.Select(p => p.Name).SequenceEqual(["MessageId", "RecipientUserId"]));
        var delivery = db.Model.FindEntityType(typeof(ChatPushDeliveryEntity))!;
        Assert.Contains(delivery.GetIndexes(), index => index.IsUnique && index.Properties.Select(p => p.Name).SequenceEqual(["OutboxId", "InstallationId"]));
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    private sealed class RecordingTokens : IChatCoreAccessTokenProvider
    {
        public List<string> Values { get; } = [];
        public string CreateToken(string scope) { Values.Add(scope); return "synthetic-service-token"; }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => response(request);
    }
}
