using TranslaCat.Chat.Infrastructure.Translation;

namespace TranslaCat.Chat.ApiTests;

public sealed class TranslationCircuitTests
{
    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public async Task Count_window_uses_ten_minimum_and_sixty_percent_failure(int failures, bool opens)
    {
        // 준비
        var circuit = new ChatExternalApiCircuitBreaker(new ManualClock());

        // 실행 — 10개 표본보다 적을 때에는 실패율과 무관하게 실행을 허용한다.
        for (int index = 0; index < 10; index++)
        {
            if (index < failures)
            {
                await FailAsync(circuit);
            }
            else
            {
                Assert.Equal(1, await SucceedAsync(circuit));
            }
        }

        // 검증
        if (opens)
        {
            await Assert.ThrowsAsync<ChatCircuitOpenException>(() => SucceedAsync(circuit));
        }
        else
        {
            Assert.Equal(1, await SucceedAsync(circuit));
        }
    }

    [Theory]
    [InlineData(60, false)]
    [InlineData(61, true)]
    public async Task Slow_call_threshold_is_strictly_over_sixty_seconds_and_one_hundred_percent(int seconds, bool opens)
    {
        // 준비
        var clock = new ManualClock();
        var circuit = new ChatExternalApiCircuitBreaker(clock);

        // 실행
        for (int index = 0; index < 10; index++)
        {
            await circuit.ExecuteAsync(_ =>
            {
                clock.Advance(TimeSpan.FromSeconds(seconds));
                return Task.FromResult(1);
            }, default);
        }

        // 검증
        if (opens)
        {
            await Assert.ThrowsAsync<ChatCircuitOpenException>(() => SucceedAsync(circuit));
        }
        else
        {
            Assert.Equal(1, await SucceedAsync(circuit));
        }
    }

    [Fact]
    public async Task Half_open_limits_concurrent_permits_to_ten_and_returns_cancelled_permit()
    {
        // 준비
        var clock = new ManualClock();
        var circuit = new ChatExternalApiCircuitBreaker(clock);
        await OpenAsync(circuit);
        clock.Advance(TimeSpan.FromSeconds(9));
        await Assert.ThrowsAsync<ChatCircuitOpenException>(() => SucceedAsync(circuit));
        clock.Advance(TimeSpan.FromSeconds(1));
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        // 실행 — 완료를 막은 10개 half-open 호출 뒤에는 11번째 호출이 실행되지 않는다.
        var cancelled = circuit.ExecuteAsync(token => gate.Task.WaitAsync(token), cancellation.Token);
        var pending = Enumerable.Range(0, 9).Select(_ => circuit.ExecuteAsync(_ => gate.Task, default)).ToArray();
        await Assert.ThrowsAsync<ChatCircuitOpenException>(() => SucceedAsync(circuit));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var replacement = circuit.ExecuteAsync(_ => gate.Task, default);
        gate.SetResult(1);

        // 검증 — 취소는 실패 표본이 아니며 대체 성공을 포함한 10개가 CLOSED로 전환한다.
        Assert.All(await Task.WhenAll(pending.Append(replacement)), value => Assert.Equal(1, value));
        Assert.Equal(1, await SucceedAsync(circuit));
    }

    [Fact]
    public async Task Half_open_failure_threshold_reopens_for_another_ten_seconds()
    {
        // 준비
        var clock = new ManualClock();
        var circuit = new ChatExternalApiCircuitBreaker(clock);
        await OpenAsync(circuit);
        clock.Advance(TimeSpan.FromSeconds(10));

        // 실행
        for (int index = 0; index < 6; index++)
        {
            await FailAsync(circuit);
        }
        for (int index = 0; index < 4; index++)
        {
            await SucceedAsync(circuit);
        }

        // 검증
        await Assert.ThrowsAsync<ChatCircuitOpenException>(() => SucceedAsync(circuit));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, await SucceedAsync(circuit));
    }

    [Fact]
    public async Task Old_inflight_failure_cannot_change_a_recovered_generation()
    {
        // 준비
        var clock = new ManualClock();
        var circuit = new ChatExternalApiCircuitBreaker(clock);
        var old = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldCall = circuit.ExecuteAsync(_ => old.Task, default);
        await OpenAsync(circuit);
        clock.Advance(TimeSpan.FromSeconds(10));
        for (int index = 0; index < 10; index++)
        {
            await SucceedAsync(circuit);
        }

        // 실행 — 오래된 실패와 새 세대 실패5/성공4를 합쳐서 회로를 열면 안 된다.
        old.SetException(new IOException("synthetic old failure"));
        await Assert.ThrowsAsync<IOException>(() => oldCall);
        for (int index = 0; index < 5; index++)
        {
            await FailAsync(circuit);
        }
        for (int index = 0; index < 4; index++)
        {
            await SucceedAsync(circuit);
        }

        // 검증
        Assert.Equal(1, await SucceedAsync(circuit));
    }

    private static Task<int> SucceedAsync(ChatExternalApiCircuitBreaker circuit)
    {
        return circuit.ExecuteAsync(_ => Task.FromResult(1), default);
    }

    private static async Task FailAsync(ChatExternalApiCircuitBreaker circuit)
    {
        await Assert.ThrowsAsync<IOException>(() => circuit.ExecuteAsync<int>(
                _ => Task.FromException<int>(new IOException("synthetic failure")), default));
    }

    private static async Task OpenAsync(ChatExternalApiCircuitBreaker circuit)
    {
        for (int index = 0; index < 10; index++)
        {
            await FailAsync(circuit);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            return Interlocked.Read(ref ticks);
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        }

        public void Advance(TimeSpan value)
        {
            Interlocked.Add(ref ticks, value.Ticks);
        }
    }
}
