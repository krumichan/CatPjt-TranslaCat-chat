using System.Net;
using System.Text;
using System.Text.Json;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Infrastructure.Core;
using TranslaCat.Chat.Infrastructure.Push;

namespace TranslaCat.Chat.ApiTests.Push;

public sealed class OpenInvitationPushProcessorTests
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
    [InlineData("switched-binding")]
    [InlineData("new-installation")]
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
            if (scenario == "switched-binding") fixture.CurrentTarget = Target() with { AccountBindingId = "different_account" };
            if (scenario == "new-installation") fixture.CurrentTarget = Target() with { InstallationId = "e205aa9f-7a40-4d7f-9249-7af77645b334" };
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
            Assert.Equal(OpenInvitationPushProcessor.Backoff(attempt), store.LastDelay);
        }
        await fixture.Processor.SweepAsync(default);

        // 검증
        Assert.Equal(5, fixture.SendCount);
        Assert.Equal("FAILED", store.Delivery!.Status);
        Assert.True(OpenInvitationPushProcessor.Backoff(100) <= 900);
    }

    [Fact]
    public async Task V2_payload_contains_only_invitation_identity_and_never_a_fake_message_or_private_details()
    {
        // 준비
        var store = new MemoryStore();
        using var fixture = new Fixture(store);

        // 실행
        await fixture.Processor.SweepAsync(default);
        using var payload = JsonDocument.Parse(Assert.Single(fixture.Payloads));
        var message = Assert.Single(payload.RootElement.EnumerateArray());
        var data = message.GetProperty("data");

        // 검증: 본문은 방명/발신자/대상 ID 없이 고정 문구만 포함한다.
        Assert.Equal("오픈채팅 초대가 도착했어요.", message.GetProperty("body").GetString());
        Assert.Equal(2, data.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("OPEN_INVITATION", data.GetProperty("kind").GetString());
        Assert.Equal("7", data.GetProperty("notificationId").GetString());
        Assert.Equal("3", data.GetProperty("roomId").GetString());
        Assert.Equal(Target().AccountBindingId, data.GetProperty("accountBindingId").GetString());
        Assert.Equal(6, data.EnumerateObject().Count());
        Assert.False(data.TryGetProperty("messageId", out _));
    }

    [Fact]
    public async Task Missing_relation_and_duplicate_registry_targets_retry_without_expanding_or_provider_call()
    {
        // 준비
        foreach (bool malformedRelation in new[] { true, false })
        {
            var store = new MemoryStore();
            using var fixture = new Fixture(store) { InvalidRelations = malformedRelation, DuplicateTarget = !malformedRelation };

            // 실행
            await fixture.Processor.SweepAsync(default);

            // 검증: 불완전한 정책/기기 응답은 허용으로 해석하지 않는다.
            Assert.Equal(0, fixture.SendCount);
            Assert.Equal(0, store.ExpansionCount);
            Assert.Equal(5, store.LastDelay);
            Assert.Null(store.Delivery);
        }
    }

    [Fact]
    public async Task Core_unavailability_retries_same_durable_event_then_recovers()
    {
        // 준비
        var store = new MemoryStore();
        using var fixture = new Fixture(store) { CoreUnavailable = true };

        // 실행
        await fixture.Processor.SweepAsync(default);
        Assert.Null(store.Delivery);
        Assert.Equal(5, store.LastDelay);
        fixture.CoreUnavailable = false;
        await fixture.Processor.SweepAsync(default);

        // 검증
        Assert.Equal(1, fixture.SendCount);
        using var json = JsonDocument.Parse(Assert.Single(fixture.Payloads));
        Assert.Equal("c5e78f48-5cc4-40ed-8b2f-5302b099ab6a", json.RootElement[0].GetProperty("data").GetProperty("eventId").GetString());
    }

    [Fact]
    public async Task Pending_receipt_has_own_attempt_limit_and_never_repeats_provider_send()
    {
        // 준비
        var store = new MemoryStore();
        using var fixture = new Fixture(store) { ReceiptPending = true };
        fixture.Options.MaxReceiptAttempts = 2;

        // 실행
        await fixture.Processor.SweepAsync(default);
        await fixture.Processor.SweepAsync(default);
        Assert.Equal("RECEIPT", store.Delivery!.Status);
        await fixture.Processor.SweepAsync(default);
        await fixture.Processor.SweepAsync(default);

        // 검증
        Assert.Equal(1, fixture.SendCount);
        Assert.Equal(2, fixture.ReceiptCount);
        Assert.Equal("FAILED", store.Delivery.Status);
    }

    [Theory]
    [InlineData("MessageTooBig")]
    [InlineData("InvalidCredentials")]
    [InlineData("MismatchSenderId")]
    public async Task Permanent_provider_failure_is_terminal_without_disabling_device(string error)
    {
        // 준비
        var store = new MemoryStore();
        using var fixture = new Fixture(store) { SendError = error };

        // 실행
        await fixture.Processor.SweepAsync(default);
        await fixture.Processor.SweepAsync(default);

        // 검증
        Assert.Equal(1, fixture.SendCount);
        Assert.Equal("FAILED", store.Delivery!.Status);
        Assert.Empty(fixture.Invalidations);
    }

    [Fact]
    public async Task Device_not_registered_ticket_invalidates_original_binding_after_registry_changes()
    {
        // 준비
        var store = new MemoryStore();
        using var fixture = new Fixture(store) { SendError = "DeviceNotRegistered" };

        // 실행
        await fixture.Processor.SweepAsync(default);
        Assert.Equal("INVALIDATE", store.Delivery!.Status);
        fixture.CurrentTarget = Target(2) with { TokenVersion = 2, AccountBindingId = "new_binding" };
        await fixture.Processor.SweepAsync(default);

        // 검증
        Assert.Equal("INVALIDATED", store.Delivery.Status);
        using var body = JsonDocument.Parse(Assert.Single(fixture.Invalidations));
        Assert.Equal(1, body.RootElement.GetProperty("generation").GetInt64());
        Assert.Equal(1, body.RootElement.GetProperty("tokenVersion").GetInt64());
        Assert.Equal("synthetic_binding", body.RootElement.GetProperty("accountBindingId").GetString());
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
        public bool CoreUnavailable { get; set; }
        public bool InvalidRelations { get; set; }
        public bool DuplicateTarget { get; set; }
        public bool ReceiptPending { get; set; }
        public string? SendError { get; set; }
        public ChatPushOptions Options { get; }
        public int ReceiptCount { get; private set; }
        public List<string> Invalidations { get; } = [];
        public OpenInvitationPushProcessor Processor { get; }

        public Fixture(MemoryStore store)
        {
            var options = new ChatPushOptions { AppEnvironment = "development", ProjectId = "55b79b33-07b9-4d4b-a3f8-cf24ea76b139" };
            Options = options;
            coreHttp = new(new Handler(async request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/invalidate"))
                {
                    Invalidations.Add(await request.Content!.ReadAsStringAsync());
                    return new(HttpStatusCode.NoContent);
                }
                if (CoreUnavailable) return new(HttpStatusCode.ServiceUnavailable);
                if (path.EndsWith("/relations/query") && InvalidRelations) return Json(new ChatCoreRelations([], []));
                if (!path.EndsWith("/relations/query") && DuplicateTarget) return Json(new ChatCorePushTargets([CurrentTarget, CurrentTarget]));
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
                    if (SendError is not null) return Json(new { data = new[] { new { status = "error", details = new { error = SendError } } } });
                    return SendStatus == HttpStatusCode.OK ? Json(new { data = new[] { new { status = "ok", id = "synthetic-ticket" } } }) : new HttpResponseMessage(SendStatus);
                }
                ReceiptCount++;
                if (ReceiptPending) return Json(new { data = new Dictionary<string, object>() });
                object receipt = ReceiptError is null ? new { status = "ok" } : new { status = "error", details = new { error = ReceiptError } };
                return Json(new { data = new Dictionary<string, object> { ["synthetic-ticket"] = receipt } });
            }));
            Processor = new(store, new(coreHttp, new(new Uri("https://synthetic.invalid"), TimeSpan.FromSeconds(1)), new Tokens()), new(expoHttp, options), options);
        }
        public void Dispose() { coreHttp.Dispose(); expoHttp.Dispose(); }
    }

    private sealed class MemoryStore : IOpenInvitationPushStore
    {
        private readonly OpenInvitationPushOutboxEntity outbox = new()
        { Id = 1, NotificationId = 7, RoomId = 3, SenderUserId = 11, RecipientUserId = 22, EventId = "c5e78f48-5cc4-40ed-8b2f-5302b099ab6a", LeaseToken = "outbox-lease" };
        public OpenInvitationPushDeliveryEntity? Delivery { get; private set; }
        public bool Eligible { get; set; } = true;
        public bool OwnsLease { get; set; } = true;
        public int ExpansionCount { get; private set; }
        public int LastDelay { get; private set; }
        public Action? OnExpanded { get; set; }
        public Task<IReadOnlyList<long>> CandidatesAsync(bool deliveries, CancellationToken token) => Task.FromResult<IReadOnlyList<long>>(
            deliveries ? Delivery?.Status is "SEND" or "RECEIPT" or "INVALIDATE" ? [1L] : [] : outbox.Status == "PENDING" ? [1L] : []);
        public Task<OpenInvitationPushOutboxEntity?> ClaimOutboxAsync(long id, CancellationToken token) { outbox.Attempts++; return Task.FromResult<OpenInvitationPushOutboxEntity?>(outbox); }
        public Task<OpenInvitationPushDeliveryEntity?> ClaimDeliveryAsync(long id, CancellationToken token) { Delivery!.Attempts++; return Task.FromResult<OpenInvitationPushDeliveryEntity?>(Delivery); }
        public Task<OpenInvitationPushOutboxEntity> OutboxAsync(long id, CancellationToken token) => Task.FromResult(outbox);
        public Task<bool> EligibleAsync(OpenInvitationPushOutboxEntity job, CancellationToken token) => Task.FromResult(Eligible);
        public Task<bool> RefreshTargetAsync(OpenInvitationPushDeliveryEntity job, ChatCorePushTarget target, CancellationToken token)
        { if (!OwnsLease || !OpenInvitationPushProcessor.Matches(job, target)) return Task.FromResult(false); job.Generation = target.Generation; return Task.FromResult(true); }
        public Task<bool> OwnsDeliveryAsync(OpenInvitationPushDeliveryEntity job, CancellationToken token) => Task.FromResult(OwnsLease);
        public Task<bool> ExpandAsync(OpenInvitationPushOutboxEntity job, IReadOnlyList<ChatCorePushTarget> targets, CancellationToken token)
        {
            ExpansionCount++;
            outbox.Status = "EXPANDED";
            var target = Assert.Single(targets);
            Delivery = new() { Id = 1, OutboxId = 1, InstallationId = target.InstallationId, AccountBindingId = target.AccountBindingId,
                Generation = target.Generation, TokenVersion = target.TokenVersion, LeaseToken = "delivery-lease" };
            OnExpanded?.Invoke();
            return Task.FromResult(true);
        }
        public Task<bool> FinishOutboxAsync(OpenInvitationPushOutboxEntity job, string status, int delaySeconds, CancellationToken token)
        { outbox.Status = status; LastDelay = delaySeconds; return Task.FromResult(true); }
        public Task<bool> FinishDeliveryAsync(OpenInvitationPushDeliveryEntity job, string status, int delaySeconds, string? ticketId, bool resetAttempts, CancellationToken token)
        { job.Status = status; job.TicketId = ticketId; if (resetAttempts) job.Attempts = 0; LastDelay = delaySeconds; return Task.FromResult(true); }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    private sealed class Tokens : IChatCoreAccessTokenProvider { public string CreateToken(string scope) => "synthetic-service-token"; }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => response(request); }
}
