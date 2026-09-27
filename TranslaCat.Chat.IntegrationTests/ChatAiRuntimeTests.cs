using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TranslaCat.Chat.Api.Ai;
using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class ChatAiRuntimeTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task Human_HTTP_can_save_a_reply_from_the_owned_Python_fixture_through_MySql_Redis_and_STOMP()
    {
        // 준비: 기본 회귀는 loopback 응답을 쓰고, 명시한 격리 실행만 실제 FastAPI로 연결한다.
        await using var settings = await ChatAiTestData.SettingsAsync(fixture, value => value with
        {
            ResponseDelayEnabled = false
        });
        var seed = await ChatAiTestData.SeedAsync(fixture);
        await SameLanguageAsync(seed.Room.MemberId);
        await using var localAi = await ChatAiHttpStub.StartAsync(ChatAiHttpStub.ValidReplyAsync);
        string? external = Environment.GetEnvironmentVariable("CHAT_FINAL_PYTHON_ORIGIN");
        bool python = !string.IsNullOrWhiteSpace(external);
        var origin = python ? new Uri(external!) : localAi.Address;
        string key = python ? Environment.GetEnvironmentVariable("CHAT_FINAL_PYTHON_KEY")! : "synthetic-ai-key";
        if (python && (Environment.GetEnvironmentVariable("CHAT_AUTH_CONTRACT_TEST") != "true"
            || origin.Host != "127.0.0.1" || origin.Scheme != "http" || string.IsNullOrWhiteSpace(key)))
        {
            throw new InvalidOperationException("Only the owned loopback Python fixture is allowed.");
        }

        var prefix = Prefix();
        await using var writer = await ChatRuntimeTestHost.StartAsync(fixture, prefix, configure: services =>
        {
            services.AddSingleton<IChatMessageProfileReader, Profiles>();
            services.AddSingleton<IChatAiUserNameReader, ChatAiTestData.Names>();
            services.AddChatAi(new ChatAiOptions
            {
                Enabled = true,
                AiBaseUri = origin,
                ApiKey = key,
                MaximumAttempts = 1,
                RetryWait = TimeSpan.Zero,
                AttemptTimeout = TimeSpan.FromSeconds(5),
                ConnectTimeout = TimeSpan.FromSeconds(1)
            });
        });
        await using var receiver = await ChatRuntimeTestHost.StartAsync(fixture, prefix);
        using var socket = await receiver.ConnectAsync(seed.Room.UserId);
        await socket.SubscribeAsync("ai", $"/topic/chat/rooms/{seed.Room.RoomId}");

        // 실행: 실제 CHAT HTTP가 worker를 깨우고 AI 응답을 DB에 commit한 뒤 STOMP로 전파한다.
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/chat/rooms/{seed.Room.RoomId}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", writer.CreateToken(seed.Room.UserId));
        request.Content = JsonContent.Create(new
        {
            content = "@Mika synthetic-runtime-save"
        });
        using var response = await writer.Client.SendAsync(request);
        var human = await socket.ReceiveEventAsync("chat.message.created");
        var answer = await socket.ReceiveEventAsync("chat.message.created");

        // 검증: 합성 Provider 출력만 대역이며 저장·실시간 adapter는 운영 구현이다.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("USER", human.GetProperty("message").GetProperty("senderType").GetString());
        var message = answer.GetProperty("message");
        Assert.Equal("AI", message.GetProperty("senderType").GetString());
        Assert.Equal("合成応答", message.GetProperty("content").GetString());
        Assert.Equal(python ? 0 : 1, localAi.Requests.Count);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.True(await db.ChatMessages.AnyAsync(row => row.Id == message.GetProperty("id").GetInt64()
            && row.AiRequestId != null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Human_HTTP_commit_runs_real_AI_worker_local_HTTP_delayed_save_and_cross_app_STOMP(bool delayed)
    {
        // 준비 — 계정 이름/프로필과 AI 추론 응답만 합성 대역이며 HTTP/DB/Redis/STOMP는 실제 경로다.
        await using var settings = await ChatAiTestData.SettingsAsync(fixture, value => value with
        {
            ResponseDelayEnabled = delayed,
            ResponseDelayMinMillis = 100,
            ResponseDelayMaxMillis = 100
        });
        var seed = await ChatAiTestData.SeedAsync(fixture);
        await SameLanguageAsync(seed.Room.MemberId);
        await using var ai = await ChatAiHttpStub.StartAsync(ChatAiHttpStub.ValidReplyAsync);
        var prefix = Prefix();
        await using var writer = await ChatRuntimeTestHost.StartAsync(fixture, prefix, configure: services => Configure(services, ai));
        await using var receiver = await ChatRuntimeTestHost.StartAsync(fixture, prefix);
        using var socket = await receiver.ConnectAsync(seed.Room.UserId);
        await socket.SubscribeAsync("ai", $"/topic/chat/rooms/{seed.Room.RoomId}");

        // 실행
        using var request = Request(writer, seed.Room.UserId, seed.Room.RoomId);
        using var response = await writer.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var human = (await socket.ReceiveEventAsync("chat.message.created")).GetProperty("message");
        var answerEvent = await socket.ReceiveEventAsync("chat.message.created");
        var answer = answerEvent.GetProperty("message");

        // 검증 — 다른 app에서 이벤트를 받은 뒤 독립 DB connection으로 실제 commit 상태를 확인한다.
        Assert.Equal("USER", human.GetProperty("senderType").GetString());
        Assert.Equal("AI", answer.GetProperty("senderType").GetString());
        Assert.Equal("合成応答", answer.GetProperty("content").GetString());
        Assert.Equal(seed.MemberId, answer.GetProperty("senderAiMemberId").GetInt64());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("senderUserId").ValueKind);
        Assert.EndsWith("Z", answerEvent.GetProperty("occurredAt").GetString());
        Assert.Single(ai.Requests);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        var saved = await db.ChatMessages.SingleAsync(row => row.Id == answer.GetProperty("id").GetInt64());
        Assert.Equal($"chat-ai:mention:{human.GetProperty("id").GetInt64()}:{seed.MemberId}", saved.AiRequestId);
        Assert.Equal(0, await db.ChatMessageTranslations.CountAsync(row => row.ChatMessageId == saved.Id));
        var activity = await db.ChatRoomAiActivities.SingleAsync(row => row.ChatRoomId == seed.Room.RoomId);
        Assert.Equal(human.GetProperty("id").GetInt64(), activity.LastHumanMessageId);
        Assert.Equal(1, activity.RevivalCycleVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_name_port_or_disabled_AI_rejects_human_write_before_commit(bool disabled)
    {
        // 준비
        var seed = await ChatAiTestData.SeedAsync(fixture);
        await SameLanguageAsync(seed.Room.MemberId);
        await using var ai = await ChatAiHttpStub.StartAsync(ChatAiHttpStub.ValidReplyAsync);
        await using var host = await ChatRuntimeTestHost.StartAsync(fixture, Prefix(), configure: services =>
            Configure(services, ai, enabled: !disabled, names: disabled));

        // 실행
        using var request = Request(host, seed.Room.UserId, seed.Room.RoomId);
        using var response = await host.Client.SendAsync(request);

        // 검증
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(ai.Requests);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(2, await db.ChatMessages.CountAsync(row => row.ChatRoomId == seed.Room.RoomId));
        Assert.False(await db.ChatRoomAiActivities.AnyAsync(row => row.ChatRoomId == seed.Room.RoomId));
    }

    private async Task SameLanguageAsync(long memberId)
    {
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        var member = await db.ChatRoomMembers.SingleAsync(row => row.Id == memberId);
        member.OriginalLanguageCode = "ja";
        member.TranslationLanguageCode = "ja";
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revival_batch_entry_runs_real_claim_HTTP_and_finish_without_interactive_delay(bool respond)
    {
        // 준비 — timer 시각 대신 batch 진입점에 고정 시각을 전달한다. Provider는 loopback 합성 서버다.
        var seed = await ChatAiTestData.SeedAsync(fixture);
        await SameLanguageAsync(seed.Room.MemberId);
        var batchNow = new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Unspecified);
        await ChatAiTestData.Store(fixture).RecordHumanAsync(new(seed.Room.SecondId, seed.Room.RoomId, batchNow.AddDays(-1)), default);
        await using (var db = await fixture.Contexts.CreateDbContextAsync())
        {
            (await db.ChatRoomAiActivities.SingleAsync(row => row.ChatRoomId == seed.Room.RoomId)).NextRevivalAt = batchNow;
            await db.SaveChangesAsync();
        }

        await using var ai = await ChatAiHttpStub.StartAsync((context, _) =>
        {
            return context.Response.WriteAsJsonAsync(new
            {
                output = new
                {
                    shouldRespond = respond,
                    reply = respond ? "合成復活" : null,
                    languageCode = respond ? "ja" : null
                },
                inputTokens = 10,
                outputTokens = 3,
                provider = "fake",
                model = "synthetic",
                providerCalls = 1
            });
        });
        await using var host = await ChatRuntimeTestHost.StartAsync(fixture, Prefix(), configure: services => Configure(services, ai));
        using var scope = host.Services.CreateScope();

        // 실행 — due 조회/claim/plan/HTTP/save/finish는 모두 production 구현이다.
        await scope.ServiceProvider.GetRequiredService<ChatAiProcessor>().ProcessDueAsync(batchNow, 1, default);

        // 검증 — SKIPPED도 stage를 진행하며, batch 시작 시각을 마지막 처리 시각으로 보존한다.
        var capture = Assert.Single(ai.Requests);
        using var payload = JsonDocument.Parse(capture.Body);
        Assert.Equal("/internal/v1/model/execute", capture.Path);
        string prompt = payload.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("\"triggerType\":\"REVIVAL\"", prompt);
        Assert.Contains("\"triggerMessage\":null", prompt);
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        var activity = await read.ChatRoomAiActivities.SingleAsync(row => row.ChatRoomId == seed.Room.RoomId);
        Assert.Equal(1, activity.RevivalStage);
        Assert.Null(activity.ClaimToken);
        Assert.Equal(batchNow, activity.LastRevivalAt);
        Assert.True(activity.NextRevivalAt >= batchNow.AddHours(72));
        Assert.Equal(respond ? 1 : 0, await read.ChatMessages.CountAsync(row => row.ChatRoomId == seed.Room.RoomId && row.SenderType == "AI"));
    }

    private static void Configure(IServiceCollection services, ChatAiHttpStub ai, bool enabled = true, bool names = true)
    {
        services.AddSingleton<IChatMessageProfileReader, Profiles>();
        if (names)
        {
            services.AddSingleton<IChatAiUserNameReader, ChatAiTestData.Names>();
        }

        services.AddChatAi(new ChatAiOptions
        {
            Enabled = enabled,
            AiBaseUri = ai.Address,
            ApiKey = "synthetic-ai-key",
            MaximumAttempts = 1,
            RetryWait = TimeSpan.Zero,
            AttemptTimeout = TimeSpan.FromSeconds(5),
            ConnectTimeout = TimeSpan.FromSeconds(1),
            RevivalInitialDelay = TimeSpan.FromHours(1)
        });
    }

    private static HttpRequestMessage Request(ChatRuntimeTestHost host, long userId, long roomId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/chat/rooms/{roomId}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.CreateToken(userId));
        request.Content = JsonContent.Create(new
        {
            content = "@Mika 合成リクエスト"
        });
        return request;
    }

    private static string Prefix()
    {
        return "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID")
        + ":ai:" + Guid.NewGuid().ToString("N");
    }

    private sealed class Profiles : IChatMessageProfileReader
    {
        public Task<ChatUserMessageProfile> GetUserAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ChatUserMessageProfile(userId, "synthetic", $"synthetic-{userId}@example.invalid", null));
        }

        public Task<string?> ResolveObjectUrlAsync(string objectKey, CancellationToken cancellationToken)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<string> GetAuditIdentityAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult("synthetic-audit");
        }
    }
}
