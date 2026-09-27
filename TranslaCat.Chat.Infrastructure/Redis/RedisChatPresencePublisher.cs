using System.Text.Json;
using TranslaCat.Chat.Application.Presence;

namespace TranslaCat.Chat.Infrastructure.Redis;

public sealed class RedisChatPresencePublisher(
    RedisChatEventBus eventBus,
    Func<ChatPresenceChanged, Task> localFanout,
    Action<string, Exception> reportFailure) : IChatPresencePublisher, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private IAsyncDisposable? subscription;

    public bool IsSubscribed => Volatile.Read(ref subscription) is not null;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (subscription is not null)
            {
                return;
            }

            subscription = await eventBus.SubscribeAsync("presence:events", ConsumeAsync,
                exception => reportFailure("presence-consume", exception), cancellationToken);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task PublishAsync(ChatPresenceChanged change, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fallbackRequired = !IsSubscribed;
        try
        {
            var subscribers = await eventBus.PublishAsync("presence:events",
                JsonSerializer.Serialize(change, JsonOptions), cancellationToken);
            fallbackRequired |= subscribers == 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            reportFailure("presence-publish", exception);
            fallbackRequired = true;
        }

        // 원본 local fallback을 유지한다. Pub/Sub의 유실/중복 가능성을 없애는 보장은 아니다.
        if (fallbackRequired)
        {
            await localFanout(change);
        }
    }

    private Task ConsumeAsync(string payload)
    {
        var change = JsonSerializer.Deserialize<ChatPresenceChanged>(payload, JsonOptions)
            ?? throw new JsonException("Presence event cannot be null.");
        return localFanout(change);
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycleGate.WaitAsync();
        try
        {
            var current = Interlocked.Exchange(ref subscription, null);
            if (current is not null)
            {
                await current.DisposeAsync();
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }
}
