using System.Net;
using System.Text;
using System.Text.Json;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Infrastructure.Core;
using TranslaCat.Chat.Infrastructure.Push;

namespace TranslaCat.Chat.ApiTests.Push;

public sealed class ChatPushProcessorTests
{
    private static ChatCorePushTarget Target(long generation = 1) => new(22, "c2a1b179-bbe5-4be9-a8c9-6206767d4d60",
        "synthetic_binding", generation, 1, "ExpoPushToken[synthetic-token]", "android");

    [Fact]
    public async Task Committed_outbox_expands_once_then_ticket_waits_for_receipt()
    {
        // 준비
        var store = new MemoryStore();
        using var fixture = new Fixture(store);

        // 실행
        await fixture.Processor.SweepAsync(default);
        Assert.Equal("RECEIPT", store.Delivery!.Status);
        Assert.Equal(900, store.LastDelay);
        await fixture.Processor.SweepAsync(default);

        // 검증
        Assert.Equal(1, store.ExpansionCount);
        Assert.Equal(1, fixture.SendCount);
        Assert.Equal(1, fixture.ReceiptCount);
        Assert.Equal("RECEIPT_OK", store.Delivery.Status);
        Assert.Null(store.Delivery.TicketId);
    }

    [Fact]
    public async Task Device_not_registered_is_durable_then_invalidates_all_original_versions()
    {
        // 준비
        var store = new MemoryStore();
        using var fixture = new Fixture(store) { ReceiptError = "DeviceNotRegistered" };

        // 실행
        await fixture.Processor.SweepAsync(default);
        await fixture.Processor.SweepAsync(default);
        Assert.Equal("INVALIDATE", store.Delivery!.Status);
        fixture.CurrentTarget = Target(generation: 2);
        await fixture.Processor.SweepAsync(default);

        // 검증: 갱신된 target을 조회해 대신 지우지 않고 receipt 원본 네 값을 그대로 전달한다.
        Assert.Equal("INVALIDATED", store.Delivery.Status);
        using var body = JsonDocument.Parse(Assert.Single(fixture.Invalidations));
        Assert.Equal(1, body.RootElement.GetProperty("generation").GetInt64());
        Assert.Equal(1, body.RootElement.GetProperty("tokenVersion").GetInt64());
        Assert.Equal("synthetic_binding", body.RootElement.GetProperty("accountBindingId").GetString());
        Assert.Equal(Target().InstallationId, body.RootElement.GetProperty("installationId").GetString());
    }

    [Theory]
    [InlineData("read")]
    [InlineData("blocked")]
    [InlineData("refreshed-token")]
    [InlineData("lost-lease")]
    public async Task Recheck_suppresses_ineligible_or_stale_target_before_provider_send(string scenario)
    {
        // 준비: 첫 sweep의 outbox 확장은 허용하고 전송 직전 상태만 바꾼다.
        var store = new MemoryStore();
        using var fixture = new Fixture(store);
        store.OnExpanded = () =>
        {
            if (scenario == "read") store.Eligible = false;
            if (scenario == "blocked") fixture.Blocked = true;
            if (scenario == "refreshed-token") fixture.CurrentTarget = Target() with { TokenVersion = 2 };
            if (scenario == "lost-lease") store.OwnsLease = false;
        };

        // 실행
        await fixture.Processor.SweepAsync(default);

        // 검증
        Assert.Equal(0, fixture.SendCount);
        Assert.NotEqual("RECEIPT", store.Delivery!.Status);
    }

    [Fact]
    public async Task Resume_generation_advance_keeps_same_binding_token_and_records_attempt_snapshot()
    {
        // 준비
        var store = new MemoryStore();
        using var fixture = new Fixture(store);
        store.OnExpanded = () => fixture.CurrentTarget = Target(2);

        // 실행
        await fixture.Processor.SweepAsync(default);

        // 검증
        Assert.Equal(1, fixture.SendCount);
        Assert.Equal(2, store.Delivery!.Generation);
        Assert.Equal("RECEIPT", store.Delivery.Status);
    }
    [Fact]
    public async Task Transient_send_failures_use_bounded_backoff_then_terminal_failure()
    {
        // 준비
        var store = new MemoryStore();
        using var fixture = new Fixture(store) { SendStatus = HttpStatusCode.TooManyRequests };

        // 실행
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            await fixture.Processor.SweepAsync(default);
            Assert.Equal(ChatPushProcessor.Backoff(attempt), store.LastDelay);
        }
        await fixture.Processor.SweepAsync(default);

        // 검증
        Assert.Equal(5, fixture.SendCount);
        Assert.Equal("FAILED", store.Delivery!.Status);
        Assert.True(ChatPushProcessor.Backoff(100) <= 900);
    }

    [Fact]
    public async Task Eligible_send_uses_one_recipient_summary_and_keeps_receipt_without_another_query()
    {
        // 준비
        var store = new MemoryStore();
        var summary = new BadgeSummaryStore();
        using var fixture = new Fixture(store, summary);

        // 실행
        await fixture.Processor.SweepAsync(default);
        await fixture.Processor.SweepAsync(default);

        // 검증
        Assert.Equal(1, summary.Calls);
        Assert.Equal(22, summary.UserId);
        Assert.Equal(1, fixture.SendCount);
        using var payload = JsonDocument.Parse(Assert.Single(fixture.Payloads));
        Assert.Equal(5, payload.RootElement[0].GetProperty("badge").GetInt32());
    }

    [Fact]
    public async Task Ineligible_send_skips_summary_and_changed_policy_during_summary_prevents_provider_send()
    {
        // 준비: 확장 뒤 무음/읽음이 되면 badge 집계까지 생략한다.
        var muted = new MemoryStore();
        var skipped = new BadgeSummaryStore();
        using var first = new Fixture(muted, skipped);
        muted.OnExpanded = () => muted.Eligible = false;

        // 실행 / 검증
        await first.Processor.SweepAsync(default);
        Assert.Equal(0, skipped.Calls);
        Assert.Equal(0, first.SendCount);

        // 준비: 요약 조회 중 정책이 변해도 마지막 정책 확인에서 전송을 막는다.
        var changed = new MemoryStore();
        var summary = new BadgeSummaryStore { OnRead = () => changed.Eligible = false };
        using var second = new Fixture(changed, summary);

        // 실행 / 검증
        await second.Processor.SweepAsync(default);
        Assert.Equal(1, summary.Calls);
        Assert.Equal(0, second.SendCount);
    }

    [Fact]
    public async Task Binding_changed_during_summary_is_rechecked_before_send()
    {
        // 준비
        var store = new MemoryStore();
        var summary = new BadgeSummaryStore();
        using var fixture = new Fixture(store, summary);
        summary.OnRead = () => fixture.CurrentTarget = Target() with { AccountBindingId = "changed-binding" };

        // 실행
        await fixture.Processor.SweepAsync(default);

        // 검증
        Assert.Equal(1, summary.Calls);
        Assert.Equal(0, fixture.SendCount);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient coreHttp;
        private readonly HttpClient expoHttp;
        public ChatCorePushTarget CurrentTarget { get; set; } = Target();
        public bool Blocked { get; set; }
        public string? ReceiptError { get; init; }
        public HttpStatusCode SendStatus { get; init; } = HttpStatusCode.OK;
        public int SendCount { get; private set; }
        public List<string> Payloads { get; } = [];
        public int ReceiptCount { get; private set; }
        public List<string> Invalidations { get; } = [];
        public ChatPushProcessor Processor { get; }

        public Fixture(MemoryStore store, BadgeSummaryStore? summaries = null)
        {
            var options = new ChatPushOptions { AppEnvironment = "development", ProjectId = "55b79b33-07b9-4d4b-a3f8-cf24ea76b139" };
            coreHttp = new(new Handler(async request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/invalidate"))
                {
                    Invalidations.Add(await request.Content!.ReadAsStringAsync());
                    return new(HttpStatusCode.NoContent);
                }
                object response = path.EndsWith("/relations/query")
                    ? new ChatCoreRelations([new(11, false, Blocked, "NONE")], [])
                    : new ChatCorePushTargets([CurrentTarget]);
                return Json(response);
            }));
            expoHttp = new(new Handler(async request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/send"))
                {
                    SendCount++;
                    Payloads.Add(await request.Content!.ReadAsStringAsync());
                    return SendStatus == HttpStatusCode.OK ? Json(new { data = new[] { new { status = "ok", id = "synthetic-ticket" } } }) : new HttpResponseMessage(SendStatus);
                }
                ReceiptCount++;
                object receipt = ReceiptError is null ? new { status = "ok" } : new { status = "error", details = new { error = ReceiptError } };
                return Json(new { data = new Dictionary<string, object> { ["synthetic-ticket"] = receipt } });
            }));
            Processor = new(store, new(coreHttp, new(new Uri("https://synthetic.invalid"), TimeSpan.FromSeconds(1)), new Tokens()), new(expoHttp, options), options, summaries);
        }
        public void Dispose() { coreHttp.Dispose(); expoHttp.Dispose(); }
    }

    private sealed class MemoryStore : IChatPushStore
    {
        private readonly ChatPushOutboxEntity outbox = new()
        { Id = 1, MessageId = 7, RoomId = 3, SenderUserId = 11, RecipientUserId = 22, EventId = "synthetic-event", LeaseToken = "outbox-lease" };
        public ChatPushDeliveryEntity? Delivery { get; private set; }
        public bool Eligible { get; set; } = true;
        public bool OwnsLease { get; set; } = true;
        public int ExpansionCount { get; private set; }
        public int LastDelay { get; private set; }
        public Action? OnExpanded { get; set; }
        public Task<IReadOnlyList<long>> CandidatesAsync(bool deliveries, CancellationToken token) => Task.FromResult<IReadOnlyList<long>>(
            deliveries ? Delivery?.Status is "SEND" or "RECEIPT" or "INVALIDATE" ? [1L] : [] : outbox.Status == "PENDING" ? [1L] : []);
        public Task<ChatPushOutboxEntity?> ClaimOutboxAsync(long id, CancellationToken token) { outbox.Attempts++; return Task.FromResult<ChatPushOutboxEntity?>(outbox); }
        public Task<ChatPushDeliveryEntity?> ClaimDeliveryAsync(long id, CancellationToken token) { Delivery!.Attempts++; return Task.FromResult<ChatPushDeliveryEntity?>(Delivery); }
        public Task<ChatPushOutboxEntity> OutboxAsync(long id, CancellationToken token) => Task.FromResult(outbox);
        public Task<bool> EligibleAsync(ChatPushOutboxEntity job, CancellationToken token) => Task.FromResult(Eligible);
        public Task<bool> RefreshTargetAsync(ChatPushDeliveryEntity job, ChatCorePushTarget target, CancellationToken token)
        { if (!OwnsLease || !ChatPushProcessor.Matches(job, target)) return Task.FromResult(false); job.Generation = target.Generation; return Task.FromResult(true); }
        public Task<bool> OwnsDeliveryAsync(ChatPushDeliveryEntity job, CancellationToken token) => Task.FromResult(OwnsLease);
        public Task<bool> ExpandAsync(ChatPushOutboxEntity job, IReadOnlyList<ChatCorePushTarget> targets, CancellationToken token)
        {
            ExpansionCount++;
            outbox.Status = "EXPANDED";
            var target = Assert.Single(targets);
            Delivery = new() { Id = 1, OutboxId = 1, InstallationId = target.InstallationId, AccountBindingId = target.AccountBindingId,
                Generation = target.Generation, TokenVersion = target.TokenVersion, LeaseToken = "delivery-lease" };
            OnExpanded?.Invoke();
            return Task.FromResult(true);
        }
        public Task<bool> FinishOutboxAsync(ChatPushOutboxEntity job, string status, int delaySeconds, CancellationToken token)
        { outbox.Status = status; LastDelay = delaySeconds; return Task.FromResult(true); }
        public Task<bool> FinishDeliveryAsync(ChatPushDeliveryEntity job, string status, int delaySeconds, string? ticketId, bool resetAttempts, CancellationToken token)
        { job.Status = status; job.TicketId = ticketId; if (resetAttempts) job.Attempts = 0; LastDelay = delaySeconds; return Task.FromResult(true); }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    private sealed class Tokens : IChatCoreAccessTokenProvider { public string CreateToken(string scope) => "synthetic-service-token"; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => response(request); }
}
