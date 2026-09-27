using TranslaCat.Chat.Application.Translation;

namespace TranslaCat.Chat.Api.Translation;

public sealed class ChatTranslationWorker(
    IServiceScopeFactory scopes,
    ChatTranslationQueue queue,
    ChatTranslationOptions options,
    ILogger<ChatTranslationWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!queue.IsConfigured)
        {
            return Task.CompletedTask;
        }

        // 요청 통지와 주기 복구가 경합하더라도 DB lease가 하나의 유효 실행만 선택한다.
        return Task.WhenAll(ConsumeAsync(stoppingToken), SweepAsync(stoppingToken));
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        await foreach (var intent in queue.ReadAllAsync(cancellationToken))
        {
            await RunAsync(processor => processor.ProcessRequestedAsync(intent, cancellationToken), cancellationToken);
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(options.InitialDelay, cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            // 원본 FAILED 50/max100, 최초30초/완료후60초를 유지하며 PENDING 복구를 별도 순회한다.
            await RunAsync(async processor =>
            {
                await processor.RetryFailedAsync(options.RetryLimit, cancellationToken);
                await processor.RecoverPendingAsync(options.RetryLimit, cancellationToken);
            }, cancellationToken);
            await Task.Delay(options.SweepInterval, cancellationToken);
        }
    }

    private async Task RunAsync(Func<ChatTranslationProcessor, Task> work, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await work(scope.ServiceProvider.GetRequiredService<ChatTranslationProcessor>());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 메시지/번역 본문, AI 주소와 인증값을 로그에 기록하지 않는다.
            logger.LogWarning("Chat translation worker failed. Failure={FailureType}", exception.GetType().Name);
        }
    }
}
