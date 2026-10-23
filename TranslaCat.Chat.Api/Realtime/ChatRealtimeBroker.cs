using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using TranslaCat.Chat.Application.Realtime;
using TranslaCat.Chat.Api.Safety;
using TranslaCat.Chat.Application.Core;

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
                // socket sendLock을 기다린 뒤 현재 상태로 준비한다. 대기 전에 만든 개인정보 snapshot을 보내지 않는다.
                await DeliverAsync(connection, subscription, async sendToken =>
                {
                    try
                    {
                        await using var scope = scopeFactory.CreateAsyncScope();
                        var access = scope.ServiceProvider.GetRequiredService<IChatRealtimeAccess>();
                        var content = scope.ServiceProvider.GetService<ChatRealtimeContentPolicy>();
                        if (content is not null) await content.RequireActiveAsync(connection.UserId, sendToken);
                        if (closureOnly)
                        {
                            await access.ValidateRoomClosureDeliveryAsync(connection.UserId, roomId, sendToken);
                            return json;
                        }
                        await access.ValidateRoomAccessAsync(connection.UserId, roomId, sendToken);
                        return content is null ? json : await content.PrepareAsync(connection.UserId, roomId, json, sendToken);
                    }
                    catch (OperationCanceledException) when (sendToken.IsCancellationRequested) { throw; }
                    catch (ChatCoreUnavailableException)
                    {
                        // 관계 조회 장애는 내용을 보내지 않되 구독을 영구 해제하지 않는다.
                        return null;
                    }
                    catch (Exception)
                    {
                        connection.Subscriptions.TryRemove(subscription.Id, out _);
                        return null;
                    }
                }, cancellationToken);
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
                await DeliverAsync(connection, subscription, async sendToken =>
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var content = scope.ServiceProvider.GetService<ChatRealtimeContentPolicy>();
                    if (content is null) return json;
                    try
                    {
                        await content.RequireActiveAsync(connection.UserId, sendToken);
                        return await content.PreparePrivateAsync(connection.UserId, json, sendToken);
                    }
                    catch (OperationCanceledException) when (sendToken.IsCancellationRequested) { throw; }
                    catch (Exception)
                    {
                        connection.StopPublishing();
                        connection.Socket.Abort();
                        return null;
                    }
                }, cancellationToken);
            }
        }
    }

    private static async Task DeliverAsync(ChatRealtimeConnection connection, ChatRealtimeSubscription subscription,
        Func<CancellationToken, Task<string?>> prepare, CancellationToken cancellationToken)
    {
        if (!connection.Subscriptions.TryGetValue(subscription.Id, out var current) || current != subscription)
        {
            return;
        }

        try
        {
            await connection.SendPreparedAsync("MESSAGE", new Dictionary<string, string>
            {
                ["destination"] = subscription.Destination,
                ["subscription"] = subscription.Id,
                ["message-id"] = Guid.NewGuid().ToString("N"),
                ["content-type"] = "application/json"
            }, async token =>
            {
                if (!connection.Subscriptions.TryGetValue(subscription.Id, out var active) || active != subscription)
                {
                    return null;
                }
                var body = await prepare(token);
                // 준비 중 해제된 구독에는 지연된 snapshot을 보내지 않는다.
                return connection.Subscriptions.TryGetValue(subscription.Id, out active) && active == subscription
                    ? body : null;
            }, cancellationToken);
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

    public Task SendAsync(string command, IReadOnlyDictionary<string, string>? headers, string body, CancellationToken cancellationToken)
        => SendPreparedAsync(command, headers, _ => Task.FromResult<string?>(body), cancellationToken);

    public async Task SendPreparedAsync(string command, IReadOnlyDictionary<string, string>? headers,
        Func<CancellationToken, Task<string?>> prepare, CancellationToken cancellationToken)
    {
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
            string? body = await prepare(timeout.Token);
            if (body is null) return;
            if (command == "MESSAGE" && Volatile.Read(ref closing) != 0) return;
            var bytes = ChatStompEncoder.Encode(command, headers, body);
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
