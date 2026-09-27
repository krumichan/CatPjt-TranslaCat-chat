namespace TranslaCat.Chat.Infrastructure.Translation;

public sealed class ChatExternalApiCircuitBreaker(TimeProvider timeProvider)
{
    private readonly object sync = new();
    private readonly Queue<(bool Failed, bool Slow)> outcomes = new();
    private CircuitState state;
    private DateTimeOffset openedAt;
    private long generation;
    private int halfOpenRemaining;

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        // 원본 Retry 바깥에서 이 메서드를 매번 호출하므로 HTTP 각 시도가 별도 CB 표본이다.
        cancellationToken.ThrowIfCancellationRequested();
        long ticket = Acquire();
        long started = timeProvider.GetTimestamp();
        try
        {
            var result = await call(cancellationToken);
            Complete(ticket, false, timeProvider.GetElapsedTime(started));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 호스트/요청 취소를 AI 장애 표본으로 만들지 않고 half-open permit을 반환한다.
            ReleaseCancelled(ticket);
            throw;
        }
        catch
        {
            Complete(ticket, true, timeProvider.GetElapsedTime(started));
            throw;
        }
    }

    private long Acquire()
    {
        lock (sync)
        {
            if (state == CircuitState.Open)
            {
                if (timeProvider.GetUtcNow() - openedAt < TimeSpan.FromSeconds(10))
                {
                    throw new ChatCircuitOpenException();
                }

                // 원본은 자동 timer 전환 없이 다음 호출에서 HALF_OPEN으로 진입한다.
                state = CircuitState.HalfOpen;
                generation++;
                halfOpenRemaining = 10;
                outcomes.Clear();
            }

            if (state == CircuitState.HalfOpen)
            {
                if (halfOpenRemaining == 0)
                {
                    throw new ChatCircuitOpenException();
                }

                halfOpenRemaining--;
            }

            return generation;
        }
    }

    private void Complete(long ticket, bool failed, TimeSpan elapsed)
    {
        lock (sync)
        {
            // 이전 세대의 in-flight 결과가 새 OPEN/HALF_OPEN 판정을 바꾸지 못한다.
            if (ticket != generation || state == CircuitState.Open)
            {
                return;
            }

            outcomes.Enqueue((failed, elapsed > TimeSpan.FromSeconds(60)));
            if (outcomes.Count > 10)
            {
                outcomes.Dequeue();
            }

            // count window10이 default minimum100을10으로 제한한다. failure60%, slow100%가 기준이다.
            if (outcomes.Count < 10)
            {
                return;
            }

            bool exceeded = outcomes.Count(value => value.Failed) >= 6 || outcomes.All(value => value.Slow);
            if (exceeded)
            {
                state = CircuitState.Open;
                openedAt = timeProvider.GetUtcNow();
                generation++;
                outcomes.Clear();
            }
            else if (state == CircuitState.HalfOpen)
            {
                state = CircuitState.Closed;
                generation++;
                outcomes.Clear();
            }
        }
    }

    private void ReleaseCancelled(long ticket)
    {
        lock (sync)
        {
            if (ticket == generation && state == CircuitState.HalfOpen)
            {
                halfOpenRemaining++;
            }
        }
    }

    private enum CircuitState
    {
        Closed, Open, HalfOpen
    }
}

public sealed class ChatCircuitOpenException() : Exception("External API circuit is open.");
