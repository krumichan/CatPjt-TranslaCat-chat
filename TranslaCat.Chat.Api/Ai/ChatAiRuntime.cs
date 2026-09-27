using System.Collections.Concurrent;
using System.Threading.Channels;
using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Api.Ai;

public sealed class ChatAiRuntime(
    IServiceScopeFactory scopes,
    ChatAiOptions options,
    TimeProvider timeProvider,
    IHostApplicationLifetime lifetime,
    Func<bool> dependenciesConfigured,
    ILogger<ChatAiRuntime> logger) : BackgroundService, IChatAiMessageDispatcher, IChatAiDelay
{
    private readonly Channel<long> channel = Channel.CreateBounded<long>(new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false
    });
    private readonly SemaphoreSlim deliverySlots = new(4, 4);
    private readonly ConcurrentDictionary<long, Task> background = new();
    private long taskSequence;
    private int extraWorkers;

    public bool IsConfigured => options.IsConfigured && dependenciesConfigured();

    public async Task RecordHumanAsync(ChatHumanMessageRecordedIntent intent, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IChatAiStore>().RecordHumanAsync(intent, cancellationToken);
    }

    public Task TriggerAsync(ChatAiTriggerRequestedIntent intent, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        cancellationToken.ThrowIfCancellationRequested();
        lifetime.ApplicationStopping.ThrowIfCancellationRequested();
        if (channel.Writer.TryWrite(intent.MessageId))
        {
            return Task.CompletedTask;
        }

        // 원본 aiExecutor는 core10/queue100 이후 최대20개까지 확장하고 포화 요청을 거절한다.
        int running = Interlocked.Increment(ref extraWorkers);
        if (running > 10)
        {
            Interlocked.Decrement(ref extraWorkers);
            throw new InvalidOperationException("AI trigger executor is saturated.");
        }

        Track(RunExtraAsync(intent.MessageId, lifetime.ApplicationStopping));
        return Task.CompletedTask;
    }

    public Task ScheduleAsync(ChatAiPlan plan, ChatAiReplyResponse response, TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lifetime.ApplicationStopping.ThrowIfCancellationRequested();
        Track(DeliverLaterAsync(plan, response, delay, lifetime.ApplicationStopping));
        return Task.CompletedTask;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsConfigured)
        {
            return Task.CompletedTask;
        }

        // 기존 Chat 외 작업과 공유하던 executor 부하는 별도 CHAT 프로세스 안에서 격리된다.
        var workers = Enumerable.Range(0, 10).Select(_ => ConsumeAsync(stoppingToken));
        return Task.WhenAll(workers.Append(RevivalAsync(stoppingToken)));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
        try
        {
            await Task.WhenAll(background.Values).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            // 프로세스 종료 시 지연된 응답은 원본처럼 영속 작업이 아니며 별도 복구 보장을 주장하지 않는다.
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        await foreach (long messageId in channel.Reader.ReadAllAsync(cancellationToken))
        {
            await RunAsync(processor => processor.ProcessRequestedAsync(messageId, cancellationToken), cancellationToken);
        }
    }

    private async Task RunExtraAsync(long firstMessageId, CancellationToken cancellationToken)
    {
        try
        {
            await RunAsync(processor => processor.ProcessRequestedAsync(firstMessageId, cancellationToken), cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                long messageId;
                try
                {
                    messageId = await channel.Reader.ReadAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ChannelClosedException)
                {
                    return;
                }

                await RunAsync(processor => processor.ProcessRequestedAsync(messageId, cancellationToken), cancellationToken);
            }
        }
        finally
        {
            Interlocked.Decrement(ref extraWorkers);
        }
    }

    private async Task RevivalAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(options.RevivalInitialDelay, timeProvider, cancellationToken);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.BatchTimeZone);
        while (!cancellationToken.IsCancellationRequested)
        {
            DateTime now = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), zone).DateTime, DateTimeKind.Unspecified);
            await RunAsync(processor => processor.ProcessDueAsync(now, options.RevivalLimit, cancellationToken), cancellationToken);
            await Task.Delay(options.RevivalInterval, timeProvider, cancellationToken);
        }
    }

    private async Task DeliverLaterAsync(ChatAiPlan plan, ChatAiReplyResponse response, TimeSpan delay, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, timeProvider, cancellationToken);
        await deliverySlots.WaitAsync(cancellationToken);
        try
        {
            await RunAsync(processor => processor.PersistAsync(plan, response, null, cancellationToken), cancellationToken);
        }
        finally
        {
            deliverySlots.Release();
        }
    }

    private async Task RunAsync(Func<ChatAiProcessor, Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<ChatAiProcessor>());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Chat AI worker failed. Failure={FailureType}", exception.GetType().Name);
        }
    }

    private void Track(Task task)
    {
        long id = Interlocked.Increment(ref taskSequence);
        background[id] = task;
        _ = task.ContinueWith(completed =>
        {
            background.TryRemove(id, out _);
            if (completed.IsFaulted)
            {
                logger.LogWarning("Chat AI background task failed. Failure={FailureType}",
                    completed.Exception?.GetBaseException().GetType().Name);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new ChatMessageDependencyUnavailableException("chat AI runtime");
        }
    }
}
