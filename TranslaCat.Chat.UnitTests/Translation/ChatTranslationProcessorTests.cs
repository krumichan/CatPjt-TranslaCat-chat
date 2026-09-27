using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Translation;

namespace TranslaCat.Chat.UnitTests.Translation;

public sealed class ChatTranslationProcessorTests
{
    private static readonly DateTime Now = new(2026, 9, 27, 1, 2, 3, DateTimeKind.Unspecified);

    [Fact]
    public async Task Successful_result_is_trimmed_and_delivered_only_after_store_finish()
    {
        // 준비
        var fake = new TranslationFake { Text = " \t번역 결과\r\n" };

        // 실행
        var result = await fake.Processor().ProcessOneAsync(10, 20, false, default);

        // 검증 — 이 대역은 port 호출 순서를 검증하며 DB commit 자체를 증명하지 않는다.
        Assert.Equal(ChatTranslationProcessResult.Completed, result);
        Assert.Equal(["claim", "translate", "finish", "deliver"], fake.Calls);
        Assert.Equal("번역 결과", fake.FinishedText);
        Assert.Equal(Now, fake.FinishedAt);
        Assert.Null(fake.Failure);
        Assert.Equal("COMPLETED", Assert.Single(fake.Events).Status);
    }

    [Fact]
    public async Task Missing_claim_skips_external_call_and_event()
    {
        // 준비
        var fake = new TranslationFake { MissingClaim = true };

        // 실행
        var result = await fake.Processor().ProcessOneAsync(10, 20, false, default);

        // 검증
        Assert.Equal(ChatTranslationProcessResult.Skipped, result);
        Assert.Equal(["claim"], fake.Calls);
    }

    [Fact]
    public async Task Late_result_rejected_by_store_has_no_event()
    {
        // 준비
        var fake = new TranslationFake { StaleResult = true };

        // 실행
        var result = await fake.Processor().ProcessOneAsync(10, null, false, default);

        // 검증
        Assert.Equal(ChatTranslationProcessResult.Skipped, result);
        Assert.Equal(["claim", "translate", "finish"], fake.Calls);
        Assert.Empty(fake.Events);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("\u3000")]
    public async Task Empty_response_is_failed_with_original_reason(string text)
    {
        // 준비
        var fake = new TranslationFake { Text = text };

        // 실행
        var result = await fake.Processor().ProcessOneAsync(10, null, false, default);

        // 검증
        Assert.Equal(ChatTranslationProcessResult.Failed, result);
        Assert.Equal("AI translation response is empty.", fake.Failure);
        Assert.Equal("FAILED", Assert.Single(fake.Events).Status);
    }

    [Fact]
    public async Task Failure_reason_is_bounded_at_1000_utf16_characters()
    {
        // 준비
        var fake = new TranslationFake { ClientFailure = new InvalidOperationException(new string('가', 1001)) };

        // 실행
        await fake.Processor().ProcessOneAsync(10, null, true, default);

        // 검증
        Assert.Equal(new string('가', 1000), fake.Failure);
        Assert.True(fake.AllowFailed);
    }

    [Fact]
    public async Task Blank_failure_message_falls_back_to_exception_type()
    {
        // 준비
        var fake = new TranslationFake { ClientFailure = new InvalidOperationException(" ") };

        // 실행
        await fake.Processor().ProcessOneAsync(10, null, false, default);

        // 검증
        Assert.Equal(nameof(InvalidOperationException), fake.Failure);
    }

    [Fact]
    public async Task Provider_retry_after_is_forwarded_to_the_existing_failed_sweep_gate()
    {
        // 준비: 첫 번역 요청에 안전한 하루 이내 재시도 지시가 도착한다.
        var fake = new TranslationFake
        {
            ClientFailure = new ChatExecutionDeferredException(
                "AI Server Chat Translation Error", TimeSpan.FromSeconds(600), new IOException("synthetic"))
        };

        // 실행
        var result = await fake.Processor().ProcessOneAsync(10, null, false, default);

        // 검증: 실패는 기록하지만 자동 재claim 시각은 별도 port 인자로 전달한다.
        Assert.Equal(ChatTranslationProcessResult.Failed, result);
        Assert.Equal(TimeSpan.FromSeconds(600), fake.RetryAfter);
        Assert.Equal("AI Server Chat Translation Error", fake.Failure);
        Assert.Equal(["claim", "translate", "finish", "deliver"], fake.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_turn_active_lease_into_failed_result()
    {
        // 준비
        using var cancellation = new CancellationTokenSource();
        var fake = new TranslationFake { OnTranslate = () => cancellation.Cancel(), ClientFailure = new OperationCanceledException() };

        // 실행
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fake.Processor().ProcessOneAsync(10, null, false, cancellation.Token));

        // 검증
        Assert.Equal(["claim", "translate"], fake.Calls);
        Assert.Empty(fake.Events);
    }

    [Fact]
    public async Task Delivery_failure_keeps_completed_state_and_reports_only_type()
    {
        // 준비
        var fake = new TranslationFake { DeliveryFailure = new IOException("synthetic secret body") };

        // 실행
        var result = await fake.Processor().ProcessOneAsync(10, null, false, default);

        // 검증
        Assert.Equal(ChatTranslationProcessResult.Completed, result);
        Assert.Equal(("COMPLETED", nameof(IOException)), Assert.Single(fake.Reports));
        Assert.Null(fake.Failure);
    }

    [Theory]
    [InlineData(null, 50)]
    [InlineData(0, 50)]
    [InlineData(-1, 50)]
    [InlineData(1, 1)]
    [InlineData(101, 100)]
    public async Task Failed_sweep_preserves_default_and_maximum_limit(int? limit, int expected)
    {
        // 준비
        var fake = new TranslationFake();

        // 실행
        var result = await fake.Processor().RetryFailedAsync(limit, default);

        // 검증
        Assert.Equal(("FAILED", expected), fake.LastQuery);
        Assert.Equal(new ChatTranslationBatchResult(1, 1, 0, 0), result);
        Assert.True(fake.AllowFailed);
    }

    [Fact]
    public async Task Pending_recovery_is_separate_from_failed_retry_and_counts_skip()
    {
        // 준비
        var fake = new TranslationFake { MissingClaim = true };

        // 실행
        var result = await fake.Processor().RecoverPendingAsync(null, default);

        // 검증
        Assert.Equal(("PENDING", 50), fake.LastQuery);
        Assert.Equal(new ChatTranslationBatchResult(1, 0, 0, 1), result);
        Assert.False(fake.AllowFailed);
    }

    [Fact]
    public async Task Requested_ids_are_deduplicated_and_message_id_is_forwarded()
    {
        // 준비
        var fake = new TranslationFake();

        // 실행
        await fake.Processor().ProcessRequestedAsync(new(1, 20, null, [10, 10]), default);

        // 검증
        Assert.Equal(1, fake.Calls.Count(call => call == "claim"));
        Assert.Equal(20, fake.ExpectedMessageId);
    }

    [Fact]
    public async Task Unconfigured_client_rejects_before_claim()
    {
        // 준비
        var fake = new TranslationFake { IsConfigured = false };

        // 실행
        await Assert.ThrowsAsync<ChatMessageDependencyUnavailableException>(() =>
            fake.Processor().ProcessOneAsync(10, null, false, default));

        // 검증
        Assert.Empty(fake.Calls);
    }

    private sealed class TranslationFake : IChatTranslationStore, IChatTranslationClient, IChatTranslationEventDelivery
    {
        public bool IsConfigured { get; init; } = true;
        public bool MissingClaim
        {
            get; init;
        }
        public bool StaleResult
        {
            get; init;
        }
        public string Text { get; init; } = "translated";
        public Exception? ClientFailure
        {
            get; init;
        }
        public Exception? DeliveryFailure
        {
            get; init;
        }
        public Action? OnTranslate
        {
            get; init;
        }
        public List<string> Calls { get; } = [];
        public List<ChatTranslationChanged> Events { get; } = [];
        public List<(string, string)> Reports { get; } = [];
        public string? FinishedText
        {
            get; private set;
        }
        public string? Failure
        {
            get; private set;
        }
        public DateTime FinishedAt
        {
            get; private set;
        }
        public TimeSpan? RetryAfter
        {
            get; private set;
        }
        public bool AllowFailed
        {
            get; private set;
        }
        public long? ExpectedMessageId
        {
            get; private set;
        }
        public (string, int) LastQuery
        {
            get; private set;
        }

        public ChatTranslationProcessor Processor()
        {
            return new(this, this, this, new(), () => Now,
            (status, failure) => Reports.Add((status, failure)));
        }

        public Task<IReadOnlyList<long>> FindCandidatesAsync(string status, int limit, CancellationToken cancellationToken)
        {
            LastQuery = (status, limit);
            return Task.FromResult<IReadOnlyList<long>>([10]);
        }

        public Task<ChatTranslationClaim?> TryClaimAsync(long translationId, long? expectedMessageId, bool allowFailed,
            TimeSpan leaseDuration, CancellationToken cancellationToken)
        {
            Calls.Add("claim");
            ExpectedMessageId = expectedMessageId;
            AllowFailed = allowFailed;
            return Task.FromResult(MissingClaim ? null : new ChatTranslationClaim(translationId, 1, 20, "token", "source", "ja"));
        }

        public Task<string> TranslateAsync(string text, string targetLanguageCode, CancellationToken cancellationToken)
        {
            Calls.Add("translate");
            OnTranslate?.Invoke();
            return ClientFailure is null ? Task.FromResult(Text) : Task.FromException<string>(ClientFailure);
        }

        public Task<ChatTranslationChanged?> TryFinishAsync(ChatTranslationClaim claim, string? translatedContent,
            string? failureReason, DateTime completedAt, CancellationToken cancellationToken,
            TimeSpan? retryAfter = null)
        {
            Calls.Add("finish");
            FinishedText = translatedContent;
            Failure = failureReason;
            FinishedAt = completedAt;
            RetryAfter = retryAfter;
            return Task.FromResult(StaleResult ? null : new ChatTranslationChanged(1, 20, 10, "ja",
                failureReason is null ? "COMPLETED" : "FAILED", translatedContent, failureReason));
        }

        public Task DeliverAsync(ChatTranslationChanged change, CancellationToken cancellationToken)
        {
            Calls.Add("deliver");
            Events.Add(change);
            return DeliveryFailure is null ? Task.CompletedTask : Task.FromException(DeliveryFailure);
        }

        public Task<ChatTranslationRetryChange> RetryAsync(long userId, long roomId, long messageId, string languageCode,
            bool processingAvailable, DateTime changedAt, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
