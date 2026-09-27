using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using TranslaCat.Chat.Application.Realtime;

namespace TranslaCat.Chat.Api.Realtime;

public sealed class ChatRealtimeBroker(IServiceScopeFactory scopeFactory)
{
    private readonly ConcurrentDictionary<string, ChatRealtimeConnection> connections = new(StringComparer.Ordinal);

    internal void Register(ChatRealtimeConnection connection)
    {
        connections.TryAdd(connection.SessionId, connection);
    }

    internal void Remove(ChatRealtimeConnection connection)
    {
        connections.TryRemove(connection.SessionId, out _);
    }

    public Task PublishRoomAsync(long roomId, string json, CancellationToken cancellationToken = default)
    {
        return PublishRoomCoreAsync(roomId, json, false, cancellationToken);
    }

    public Task PublishRoomClosureAsync(long roomId, string json, CancellationToken cancellationToken = default)
    {
        using var payload = JsonDocument.Parse(json);
        if (payload.RootElement.GetProperty("eventType").GetString() != "chat.room.closed"
            || payload.RootElement.GetProperty("roomId").GetInt64() != roomId)
        {
            throw new JsonException("Invalid room closure payload.");
        }
        return PublishRoomCoreAsync(roomId, json, true, cancellationToken);
    }

    private async Task PublishRoomCoreAsync(long roomId, string json, bool closureOnly, CancellationToken cancellationToken)
    {
        var destination = $"/topic/chat/rooms/{roomId.ToString(CultureInfo.InvariantCulture)}";
        foreach (var connection in connections.Values)
        {
            foreach (var subscription in connection.Subscriptions.Values.Where(value => value.Destination == destination))
            {
                // 탈퇴/강퇴 뒤 남은 구독으로 새 방 메시지를 받지 않도록 전달 직전에도 확인한다.
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var access = scope.ServiceProvider.GetRequiredService<IChatRealtimeAccess>();
                    if (closureOnly)
                    {
                        await access.ValidateRoomClosureDeliveryAsync(connection.UserId, roomId, cancellationToken);
                    }
                    else
                    {
                        await access.ValidateRoomAccessAsync(connection.UserId, roomId, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    connection.Subscriptions.TryRemove(subscription.Id, out _);
                    continue;
                }

                await DeliverAsync(connection, subscription, json, cancellationToken);
                if (closureOnly)
                {
                    connection.Subscriptions.TryRemove(subscription.Id, out _);
                }
            }
        }
    }

    public async Task PublishUserAsync(string email, string queue, string json, CancellationToken cancellationToken = default)
    {
        if (!ChatRealtimeDestination.TryParseSubscription("/user" + queue, out var destination) || destination.IsRoomTopic)
        {
            throw new ArgumentException("Unsupported CHAT user queue.", nameof(queue));
        }

        // 내부 publisher가 정한 수신자에게만 보낸다. 강퇴 알림도 전달해야 하므로 private queue는 재가입을 요구하지 않는다.
        foreach (var connection in connections.Values.Where(value => string.Equals(value.Email, email, StringComparison.Ordinal)))
        {
            foreach (var subscription in connection.Subscriptions.Values.Where(value => value.Destination == "/user" + queue))
            {
                await DeliverAsync(connection, subscription, json, cancellationToken);
            }
        }
    }

    private static async Task DeliverAsync(ChatRealtimeConnection connection, ChatRealtimeSubscription subscription, string json, CancellationToken cancellationToken)
    {
        if (!connection.Subscriptions.TryGetValue(subscription.Id, out var current) || current != subscription)
        {
            return;
        }

        try
        {
            await connection.SendAsync("MESSAGE", new Dictionary<string, string>
            {
                ["destination"] = subscription.Destination,
                ["subscription"] = subscription.Id,
                ["message-id"] = Guid.NewGuid().ToString("N"),
                ["content-type"] = "application/json"
            }, json, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            connection.Socket.Abort();
        }
    }
}

internal sealed record ChatRealtimeSubscription(string Id, string Destination);

internal sealed class ChatRealtimeConnection(WebSocket socket, long userId, string email)
{
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private int closing;
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public WebSocket Socket { get; } = socket;
    public long UserId { get; } = userId;
    public string Email { get; } = email;
    public ConcurrentDictionary<string, ChatRealtimeSubscription> Subscriptions { get; } = new(StringComparer.Ordinal);

    public void StopPublishing()
    {
        Interlocked.Exchange(ref closing, 1);
    }

    public async Task SendAsync(string command, IReadOnlyDictionary<string, string>? headers, string body, CancellationToken cancellationToken)
    {
        var bytes = ChatStompEncoder.Encode(command, headers, body);
        // 동일 socket의 동시에 발생한 room/user 이벤트와 receipt를 하나씩 보낸다.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await sendLock.WaitAsync(timeout.Token);
        try
        {
            if (command == "MESSAGE" && Volatile.Read(ref closing) != 0)
            {
                return;
            }
            await Socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public async Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        StopPublishing();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await sendLock.WaitAsync(timeout.Token);
        try
        {
            await Socket.CloseOutputAsync(status, description, timeout.Token);
        }
        finally
        {
            sendLock.Release();
        }
    }
}

internal sealed record ChatRealtimeDestination(long? RoomId, bool IsRoomTopic)
{
    public static bool TryParseSubscription(string value, out ChatRealtimeDestination destination)
    {
        destination = new(null, false);
        if (value is "/user/queue/chat/read" or "/user/queue/chat/notifications" or "/user/queue/errors")
        {
            return true;
        }
        if (TryParseId(value, "/topic/chat/rooms/", "", out var roomId))
        {
            destination = new(roomId, true);
            return true;
        }
        if (TryParseId(value, "/user/queue/chat/open-rooms/", "", out roomId))
        {
            destination = new(roomId, false);
            return true;
        }
        return false;
    }

    public static bool TryParseSend(string value, out long roomId)
    {
        return TryParseId(value, "/app/chat/rooms/", "/messages", out roomId);
    }

    private static bool TryParseId(string value, string prefix, string suffix, out long roomId)
    {
        roomId = 0;
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || !value.EndsWith(suffix, StringComparison.Ordinal)
            || value.Length <= prefix.Length + suffix.Length)
        {
            return false;
        }
        var number = value.AsSpan(prefix.Length, value.Length - prefix.Length - suffix.Length);
        return long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out roomId) && roomId > 0;
    }
}
