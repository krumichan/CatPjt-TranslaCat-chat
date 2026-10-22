using TranslaCat.Chat.Infrastructure.Push;

namespace TranslaCat.Chat.Api.Push;

public static class ChatPushExtensions
{
    public static IServiceCollection AddChatPush(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Chat:Push").Get<ChatPushOptions>() ?? new();
        options.Validate();
        int coreTimeout = configuration.GetValue<int?>("Chat:Core:TimeoutSeconds") ?? 5;
        if (options.Enabled && options.LeaseSeconds < coreTimeout * 2 + options.RequestTimeoutSeconds + 10)
            throw new InvalidOperationException("Chat push lease must cover bounded core and provider calls.");
        services.AddSingleton(options);
        if (!options.Enabled) return services;

        // 명시적으로 켠 worker는 DB와 인증된 core adapter 설정을 필요로 한다. secret 기본값은 만들지 않는다.
        if (string.IsNullOrWhiteSpace(configuration["Chat:Database:ConnectionString"])
            || !configuration.GetValue<bool>("Chat:Core:Enabled"))
        {
            throw new InvalidOperationException("Chat push requires configured database and core adapters.");
        }

        services.AddHttpClient<ExpoChatPushClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RedactLoggedHeaders(["Authorization"])
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds)
            });
        services.AddScoped<IChatPushStore, EfChatPushStore>();
        services.AddScoped<ChatPushProcessor>();
        services.AddHostedService<ChatPushWorker>();
        services.AddScoped<IOpenInvitationPushStore, EfOpenInvitationPushStore>();
        services.AddScoped<OpenInvitationPushProcessor>();
        services.AddHostedService<OpenInvitationPushWorker>();
        return services;
    }
}

public sealed class ChatPushWorker(IServiceScopeFactory scopes, ChatPushOptions options, ILogger<ChatPushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SweepSeconds));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ChatPushProcessor>().SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                // 사용자/메시지/설치/ticket/provider 원문을 기록하지 않는다. lease 만료 후 다음 sweep이 복구한다.
                logger.LogWarning("Chat push sweep failed. Failure={FailureType}", error.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public sealed class OpenInvitationPushWorker(IServiceScopeFactory scopes, ChatPushOptions options, ILogger<OpenInvitationPushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SweepSeconds));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<OpenInvitationPushProcessor>().SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                // 사용자/초대/설치/ticket/provider 원문을 기록하지 않는다. lease 만료 후 다음 sweep이 복구한다.
                logger.LogWarning("Open invitation push sweep failed. Failure={FailureType}", error.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
