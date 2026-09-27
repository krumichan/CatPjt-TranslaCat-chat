using System.Text.Json;
using TranslaCat.Chat.Api.Realtime;
using TranslaCat.Chat.Infrastructure.Redis;

namespace TranslaCat.Chat.Api.Runtime;

public sealed class ChatRealtimeRedisRelay(
    RedisChatEventBus events,
    ChatRealtimeBroker broker,
    ILogger<ChatRealtimeRedisRelay> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private IAsyncDisposable? subscription;
    public bool IsSubscribed => Volatile.Read(ref subscription) is not null;

    public Task PublishRoomAsync(long roomId, string payload, CancellationToken cancellationToken)
    {
        return PublishAsync(new(roomId, null, null, payload), cancellationToken);
    }

    public Task PublishUserAsync(string email, string queue, string payload, CancellationToken cancellationToken)
    {
        return PublishAsync(new(null, email, queue, payload), cancellationToken);
    }

    public Task PublishRoomClosureAsync(long roomId, string payload, CancellationToken cancellationToken)
    {
        return PublishAsync(new(roomId, null, null, payload, true), cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 하나의 app에서 한 subscription을 유지한다. multiplexer가 Redis 재접속 후 재구독한다.
                subscription ??= await events.SubscribeAsync("realtime:events", ConsumeAsync, ReportFailure, stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task PublishAsync(RoutedEvent message, CancellationToken cancellationToken)
    {
        var fallback = !IsSubscribed;
        try
        {
            fallback |= await events.PublishAsync("realtime:events", JsonSerializer.Serialize(message, Json), cancellationToken) == 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportFailure(exception);
            fallback = true;
        }

        // Pub/Sub 유실 시 같은 app의 연결에만 최선을 다해 전달한다. durable replay/exactly-once 보장은 없다.
        if (fallback)
        {
            await DeliverLocalAsync(message, cancellationToken);
        }
    }

    private Task ConsumeAsync(string payload)
    {
        var message = JsonSerializer.Deserialize<RoutedEvent>(payload, Json) ?? throw new JsonException();
        return DeliverLocalAsync(message, CancellationToken.None);
    }

    private Task DeliverLocalAsync(RoutedEvent message, CancellationToken cancellationToken)
    {
        if (message.RoomId is long roomId && message.Email is null && message.Queue is null)
        {
            if (message.ClosureOnly)
            {
                // 내부 전용 종료 route로 일반 메시지 접근 정책을 우회할 수 없게 payload 종류도 제한한다.
                using var payload = JsonDocument.Parse(message.Payload);
                if (payload.RootElement.GetProperty("eventType").GetString() != "chat.room.closed"
                    || payload.RootElement.GetProperty("roomId").GetInt64() != roomId)
                {
                    throw new JsonException("Invalid room closure payload.");
                }
                return broker.PublishRoomClosureAsync(roomId, message.Payload, cancellationToken);
            }
            return broker.PublishRoomAsync(roomId, message.Payload, cancellationToken);
        }

        if (message.RoomId is null && !string.IsNullOrWhiteSpace(message.Email) && message.Queue is not null)
        {
            return broker.PublishUserAsync(message.Email, message.Queue, message.Payload, cancellationToken);
        }

        throw new JsonException("Invalid CHAT event route.");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            var current = Interlocked.Exchange(ref subscription, null);
            if (current is not null)
            {
                await current.DisposeAsync();
            }
        }
    }

    private void ReportFailure(Exception exception)
    {
        logger.LogWarning("Chat realtime relay failed ({FailureType}).", exception.GetType().Name);
    }

    private sealed record RoutedEvent(long? RoomId, string? Email, string? Queue, string Payload, bool ClosureOnly = false);
}
