using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.UnitTests.Ai;

public sealed class ChatAiProcessorTests
{
    [Theory]
    [InlineData("mismatch", true, "reply", "ja", ChatAiProcessingResult.Failed)]
    [InlineData("request", false, null, null, ChatAiProcessingResult.Skipped)]
    [InlineData("request", true, " ", "ja", ChatAiProcessingResult.Failed)]
    [InlineData("request", true, "reply", "ko", ChatAiProcessingResult.Failed)]
    [InlineData("request", true, "reply", "JA", ChatAiProcessingResult.Responded)]
    public async Task Response_identity_shouldRespond_content_and_language_are_validated(
        string id, bool shouldRespond, string? reply, string? language, ChatAiProcessingResult expected)
    {
        // 준비
        var fake = new Fake { Response = new(id, shouldRespond, reply, language) };

        // 실행
        var result = await fake.Processor().ProcessPlanAsync(Plan(), null, default);

        // 검증
        Assert.Equal(expected, result);
        Assert.Equal(expected == ChatAiProcessingResult.Responded ? 1 : 0, fake.Saves);
    }

    [Fact]
    public async Task Existing_request_does_not_call_provider_or_save()
    {
        // 준비
        var fake = new Fake { Exists = true };

        // 실행
        var result = await fake.Processor().ProcessPlanAsync(Plan(), null, default);

        // 검증
        Assert.Equal(ChatAiProcessingResult.Duplicate, result);
        Assert.Equal(0, fake.Calls);
        Assert.Equal(0, fake.Saves);
    }

    [Fact]
    public async Task Interactive_response_calls_provider_before_scheduling_and_does_not_save_yet()
    {
        // 준비
        var fake = new Fake();

        // 실행
        await fake.Processor().ProcessRequestedAsync(10, default);

        // 검증 — 대역은 호출 순서만 증명하며 실제 timer/DB를 실행한 결과는 아니다.
        Assert.Equal(["provider", "schedule"], fake.Order);
        Assert.Equal(0, fake.Saves);
        Assert.Equal(TimeSpan.FromMilliseconds(1200), fake.Delay);
    }

    [Fact]
    public async Task Disabled_delay_saves_immediately_after_provider()
    {
        // 준비
        var fake = new Fake { Settings = ChatAiSystemPolicy.Defaults with { ResponseDelayEnabled = false } };

        // 실행
        await fake.Processor().ProcessRequestedAsync(10, default);

        // 검증
        Assert.Equal(["provider", "save"], fake.Order);
    }

    [Fact]
    public async Task Scheduler_failure_falls_back_to_immediate_save()
    {
        // 준비
        var fake = new Fake { ScheduleFails = true };

        // 실행
        await fake.Processor().ProcessRequestedAsync(10, default);

        // 검증
        Assert.Equal(["provider", "schedule", "save"], fake.Order);
        Assert.Equal(1, fake.Saves);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_failed_AI_result()
    {
        // 준비
        using var cancellation = new CancellationTokenSource();
        var fake = new Fake { ProviderAction = () => cancellation.Cancel(), ProviderFailure = new OperationCanceledException() };

        // 실행
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fake.Processor().ProcessPlanAsync(Plan(), null, cancellation.Token));

        // 검증
        Assert.Equal(0, fake.Saves);
    }

    [Fact]
    public async Task Revival_failure_forwards_provider_retry_after_to_existing_schedule()
    {
        // 준비: 실패한 Revival은 응답을 저장하지 않고 안전한 대기시간만 전달한다.
        var fake = new Fake
        {
            ProviderFailure = new ChatExecutionDeferredException(
                "AI Server Chat Reply Error", TimeSpan.FromSeconds(600), new IOException("synthetic"))
        };

        // 실행
        await fake.Processor().ProcessDueAsync(new DateTime(2026, 9, 27), 1, default);

        // 검증: 기존 실패 예약 port는 한 번만 호출되고 추가 Provider 요청은 없다.
        Assert.Equal(1, fake.Calls);
        Assert.Equal(0, fake.Saves);
        Assert.Equal(ChatAiProcessingResult.Failed, fake.FinishedResult);
        Assert.Equal(TimeSpan.FromSeconds(600), fake.FinishedRetryAfter);
    }

    internal static ChatAiPlan Plan()
    {
        return new(3, new("request", "MENTION", new(1, "GROUP", "synthetic", null),
        new(3, "Mika", null, "synthetic persona", "ja"), new(10, "member-1", "Member", "@Mika hello", new(2026, 9, 27)),
        [], 30, 12000, 800));
    }

    private sealed class Fake : IChatAiStore, IChatAiReplyClient, IChatAiDelay
    {
        public bool IsConfigured => true;
        public bool Exists
        {
            get; init;
        }
        public bool ScheduleFails
        {
            get; init;
        }
        public int Calls
        {
            get; private set;
        }
        public int Saves
        {
            get; private set;
        }
        public TimeSpan Delay
        {
            get; private set;
        }
        public TimeSpan? FinishedRetryAfter
        {
            get; private set;
        }
        public ChatAiProcessingResult? FinishedResult
        {
            get; private set;
        }
        public Action? ProviderAction
        {
            get; init;
        }
        public Exception? ProviderFailure
        {
            get; init;
        }
        public List<string> Order { get; } = [];
        public ChatAiReplyResponse Response { get; init; } = new("request", true, "reply", "ja");
        public ChatAiSystemSettings Settings { get; init; } = ChatAiSystemPolicy.Defaults;
        public ChatAiProcessor Processor()
        {
            return new(this, this, this, () => 0, (_, _) => { });
        }

        public Task<bool> ExistsReplyAsync(string requestId, CancellationToken token)
        {
            return Task.FromResult(Exists);
        }

        public Task<ChatAiSystemSettings> ReadSettingsAsync(CancellationToken token)
        {
            return Task.FromResult(Settings);
        }

        public Task<IReadOnlyList<ChatAiPlan>> PlanAsync(long messageId, CancellationToken token)
        {
            return Task.FromResult<IReadOnlyList<ChatAiPlan>>([Plan()]);
        }

        public Task<ChatAiReplyResponse?> GenerateAsync(ChatAiReplyRequest request, CancellationToken token)
        {
            Calls++;
            Order.Add("provider");
            ProviderAction?.Invoke();
            return ProviderFailure is null ? Task.FromResult<ChatAiReplyResponse?>(Response) : Task.FromException<ChatAiReplyResponse?>(ProviderFailure);
        }
        public Task<ChatAiProcessingResult> SaveReplyAsync(ChatAiPlan plan, string reply, ChatAiRevivalClaim? claim, CancellationToken token)
        {
            Saves++;
            Order.Add("save");
            return Task.FromResult(ChatAiProcessingResult.Responded);
        }
        public Task ScheduleAsync(ChatAiPlan plan, ChatAiReplyResponse response, TimeSpan delay, CancellationToken token)
        {
            Order.Add("schedule");
            Delay = delay;
            return ScheduleFails ? Task.FromException(new IOException("synthetic scheduler")) : Task.CompletedTask;
        }
        public Task<ChatAiPlan?> PlanRevivalAsync(ChatAiRevivalClaim claim, CancellationToken token)
        {
            return Task.FromResult<ChatAiPlan?>(Plan());
        }

        public Task RecordHumanAsync(ChatHumanMessageRecordedIntent intent, CancellationToken token)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<long>> FindDueAsync(DateTime now, int limit, CancellationToken token)
        {
            return Task.FromResult<IReadOnlyList<long>>([1]);
        }

        public Task<ChatAiRevivalClaim?> ClaimRevivalAsync(long id, DateTime now, CancellationToken token)
        {
            return Task.FromResult<ChatAiRevivalClaim?>(new(1, 1, 3, "synthetic-token", 1, 1, "request"));
        }

        public Task FinishRevivalAsync(ChatAiRevivalClaim claim, ChatAiProcessingResult result, DateTime now,
            CancellationToken token, TimeSpan? retryAfter = null)
        {
            FinishedResult = result;
            FinishedRetryAfter = retryAfter;
            return Task.CompletedTask;
        }
    }
}
