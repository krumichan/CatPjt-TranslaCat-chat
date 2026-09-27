using TranslaCat.Chat.Application.Presence;
using TranslaCat.Chat.Infrastructure.Redis;

namespace TranslaCat.Chat.Api.Presence;

public sealed class ChatPresenceHostedService(
    ChatPresenceCoordinator coordinator,
    RedisChatPresencePublisher publisher,
    ChatPresenceOptions options,
    TimeProvider timeProvider,
    ILogger<ChatPresenceHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan GracePollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SubscriptionRetryInterval = TimeSpan.FromSeconds(5);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        options.Validate();
        if (!options.Enabled)
        {
            return Task.CompletedTask;
        }

        // grace deadline 이전에는 판정하지 않으며 100ms polling과 실행 부하만큼 늦어질 수 있다.
        // singleton coordinator는 local socket만 추적한다. scoped DbContext를 보관하지 않는다.
        return Task.WhenAll(
            RunLoopAsync("refresh", options.RefreshInterval, coordinator.RefreshLocalSessionsAsync, false, stoppingToken),
            RunLoopAsync("offline-grace", GracePollInterval, coordinator.VerifyDueOfflineAsync, false, stoppingToken),
            RunLoopAsync("subscribe", SubscriptionRetryInterval, publisher.StartAsync, true, stoppingToken));
    }

    private async Task RunLoopAsync(
        string operation,
        TimeSpan interval,
        Func<CancellationToken, Task> action,
        bool runImmediately,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!runImmediately)
                {
                    await Task.Delay(interval, timeProvider, stoppingToken);
                }

                runImmediately = false;
                await action(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // 다음 fixed-delay 시도를 유지한다. payload/연결 문자열/예외 원문은 로그에 싣지 않는다.
                logger.LogWarning("Chat presence worker operation {Operation} failed ({FailureType}).",
                    operation, exception.GetType().Name);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 진행 중 operation 종료를 기다린 뒤 subscription을 닫는다. Redis lease는 TTL도 가진다.
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            await publisher.DisposeAsync();
        }
    }
}
