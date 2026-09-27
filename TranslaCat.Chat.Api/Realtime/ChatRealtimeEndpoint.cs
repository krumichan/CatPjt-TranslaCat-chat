using System.Globalization;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using TranslaCat.Chat.Api.Authentication;
using TranslaCat.Chat.Api.ServiceAuthentication;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Read;
using TranslaCat.Chat.Application.Realtime;

namespace TranslaCat.Chat.Api.Realtime;

public sealed class ChatRealtimeEndpoint(IServiceScopeFactory scopeFactory, ChatRealtimeBroker broker,
    TimeProvider timeProvider, ChatRealtimeOptions options)
{
    public async Task HandleAsync(HttpContext context)
    {
        // 운영 브라우저 origin은 명시 목록을 요구한다. Origin 없는 내부 연결도 별도 서비스 인증을 통과해야 한다.
        if (!context.RequestServices.GetRequiredService<IHostEnvironment>().IsEnvironment("Testing")
            && context.Request.Headers.ContainsKey("Origin")
            && (context.Request.Headers.Origin.Count != 1
                || !options.AllowedOrigins.Contains(context.Request.Headers.Origin.ToString(), StringComparer.Ordinal)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // native WebSocket만 이 endpoint에서 처리한다. SockJS HTTP fallback을 성공처럼 응답하지 않는다.
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var offered = context.WebSockets.WebSocketRequestedProtocols;
        var protocol = offered.Contains("v12.stomp") ? "v12.stomp" : offered.Contains("v11.stomp") ? "v11.stomp" : null;
        if (offered.Count > 0 && protocol is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(protocol);
        var decoder = new ChatStompDecoder();
        var buffer = new byte[8192];
        ChatRealtimeConnection? connection = null;
        var lifecycleConnected = false;
        using var connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        connectionLifetime.CancelAfter(options.ConnectTimeout);

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(), connectionLifetime.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    if (connection is not null)
                    {
                        await connection.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closing.Token);
                    }
                    else
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closing.Token);
                    }
                    break;
                }
                if (received.MessageType != WebSocketMessageType.Text)
                {
                    throw new ChatStompProtocolException();
                }

                foreach (var frame in decoder.Append(buffer.AsSpan(0, received.Count)))
                {
                    if (connection is null)
                    {
                        connection = await ConnectAsync(context, socket, frame, connectionLifetime.Token);
                        connectionLifetime.CancelAfter(Timeout.InfiniteTimeSpan);

                        // Presence는 별도 실제 adapter가 등록된 경우에만 연결한다.
                        await using var scope = scopeFactory.CreateAsyncScope();
                        if (scope.ServiceProvider.GetService<IChatRealtimeSessionLifecycle>() is { } lifecycle)
                        {
                            // 등록 도중 취소/실패해도 같은 session의 부분적인 Presence 등록을 정리한다.
                            lifecycleConnected = true;
                            await lifecycle.ConnectAsync(connection.SessionId, connection.UserId, connectionLifetime.Token);
                        }
                        broker.Register(connection);
                        await connection.SendAsync("CONNECTED", new Dictionary<string, string>
                        {
                            ["version"] = NegotiateVersion(frame),
                            ["session"] = connection.SessionId,
                            ["heart-beat"] = "0,0"
                        }, "", connectionLifetime.Token);
                        continue;
                    }

                    if (!await ProcessAsync(connection, frame, connectionLifetime.Token))
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (connectionLifetime.IsCancellationRequested)
        {
            socket.Abort();
        }
        catch (WebSocketException)
        {
            socket.Abort();
        }
        catch (Exception exception)
        {
            // payload, JWT, 내부 예외를 오류 frame에 되돌려 보내지 않는다.
            await SendErrorAndCloseAsync(socket, connection, exception);
        }
        finally
        {
            if (connection is not null)
            {
                connection.StopPublishing();
                broker.Remove(connection);
                if (lifecycleConnected)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await using var scope = scopeFactory.CreateAsyncScope();
                        var lifecycle = scope.ServiceProvider.GetService<IChatRealtimeSessionLifecycle>();
                        if (lifecycle is not null)
                        {
                            await lifecycle.DisconnectAsync(connection.SessionId, connection.UserId, cleanup.Token);
                        }
                    }
                    catch (Exception)
                    {
                        // 연결 종료에 실패해도 인증/구독을 되살리지 않는다. 실제 presence의 TTL 수습은 adapter 책임이다.
                    }
                }
            }
        }
    }

    private async Task<ChatRealtimeConnection> ConnectAsync(HttpContext context, WebSocket socket, ChatStompFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Command is not ("CONNECT" or "STOMP"))
        {
            throw new ChatStompProtocolException();
        }
        _ = NegotiateVersion(frame);
        var authorization = Required(frame, "Authorization");
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            throw new ChatStompProtocolException();
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var authenticator = scope.ServiceProvider.GetRequiredService<ChatJwtAuthenticator>();
        var principal = await authenticator.AuthenticateAsync(authorization[7..], cancellationToken);
        if (principal?.Identity?.IsAuthenticated != true
            || !long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var userId)
            || string.IsNullOrWhiteSpace(principal.Identity.Name))
        {
            throw new ChatStompProtocolException();
        }

        // HTTP handshake 서비스 신원과 STOMP 사용자 신원을 모두 확인한 뒤 session을 만든다.
        var ingress = scope.ServiceProvider.GetService<ChatServiceIngressGuard>();
        if (ingress is not null && !ingress.ValidateRealtimeUser(context, principal))
        {
            throw new ChatStompProtocolException();
        }
        return new ChatRealtimeConnection(socket, userId, principal.Identity.Name);
    }

    private async Task<bool> ProcessAsync(ChatRealtimeConnection connection, ChatStompFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Command)
        {
            case "SUBSCRIBE":
                {
                    var id = Required(frame, "id");
                    var destination = Required(frame, "destination");
                    if (connection.Subscriptions.Count >= 128 || !ChatRealtimeDestination.TryParseSubscription(destination, out var parsed)
                        || (frame.Headers.TryGetValue("ack", out var ack) && ack != "auto"))
                    {
                        throw new ChatStompProtocolException();
                    }
                    if (parsed.RoomId is { } roomId)
                    {
                        await ValidateAccessAsync(connection.UserId, roomId, cancellationToken);
                    }
                    if (!connection.Subscriptions.TryAdd(id, new(id, destination)))
                    {
                        throw new ChatStompProtocolException();
                    }
                    break;
                }
            case "UNSUBSCRIBE":
                connection.Subscriptions.TryRemove(Required(frame, "id"), out _);
                break;
            case "SEND":
                {
                    if (!ChatRealtimeDestination.TryParseSend(Required(frame, "destination"), out var roomId))
                    {
                        throw new ChatStompProtocolException();
                    }
                    // 원본 inbound interceptor의 접근 검사와 Controller 메시지 처리 오류의 경계를 보존한다.
                    await ValidateAccessAsync(connection.UserId, roomId, cancellationToken);
                    if (!await SendMessageAsync(connection, frame, roomId, cancellationToken))
                    {
                        return true;
                    }
                    break;
                }
            case "DISCONNECT":
                connection.StopPublishing();
                await SendReceiptAsync(connection, frame, cancellationToken);
                await connection.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken);
                return false;
            default:
                throw new ChatStompProtocolException();
        }

        await SendReceiptAsync(connection, frame, cancellationToken);
        return true;
    }

    private async Task ValidateAccessAsync(long userId, long roomId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var access = scope.ServiceProvider.GetRequiredService<IChatRealtimeAccess>();
        await access.ValidateRoomAccessAsync(userId, roomId, cancellationToken);
    }

    private async Task<bool> SendMessageAsync(ChatRealtimeConnection connection, ChatStompFrame frame, long roomId, CancellationToken cancellationToken)
    {
        try
        {
            using var payload = JsonDocument.Parse(frame.Body);
            if (payload.RootElement.ValueKind != JsonValueKind.Object
                || !payload.RootElement.TryGetProperty("content", out var value))
            {
                throw new ChatStompProtocolException();
            }
            // 기존 Jackson String DTO의 숫자/boolean scalar 변환을 유지하고 object/array/null은 거부한다.
            var content = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString()!,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                _ => throw new ChatStompProtocolException()
            };
            if (IsJavaBlank(content) || content.Length > 5000)
            {
                throw new ChatStompProtocolException();
            }

            // Application이 저장/commit/전달을 소유한다. transport가 성공 메시지를 재발행하지 않는다.
            await using var scope = scopeFactory.CreateAsyncScope();
            var sender = scope.ServiceProvider.GetRequiredService<IChatRealtimeMessageSender>();
            await sender.SendTextAsync(connection.UserId, roomId, content, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ChatMessageException exception)
        {
            await broker.PublishUserAsync(connection.Email, "/queue/errors", CreateErrorBody(
                "CHAT_WEBSOCKET_BUSINESS_ERROR", exception.Message), cancellationToken);
            return false;
        }
        catch (Exception)
        {
            // 원본 Controller의 generic message exception은 연결을 닫지 않고 사용자 errors queue로 보낸다.
            await broker.PublishUserAsync(connection.Email, "/queue/errors", CreateErrorBody(
                "CHAT_WEBSOCKET_INTERNAL_ERROR", "WebSocket 메시지 처리 중 오류가 발생했습니다."), cancellationToken);
            return false;
        }
    }

    private static Task SendReceiptAsync(ChatRealtimeConnection connection, ChatStompFrame frame, CancellationToken cancellationToken)
    {
        return frame.Headers.TryGetValue("receipt", out var receipt)
                ? connection.SendAsync("RECEIPT", new Dictionary<string, string> { ["receipt-id"] = receipt }, "", cancellationToken)
                : Task.CompletedTask;
    }

    private static string NegotiateVersion(ChatStompFrame frame)
    {
        var versions = Required(frame, "accept-version").Split(',');
        return versions.Contains("1.2") ? "1.2" : versions.Contains("1.1") ? "1.1" : throw new ChatStompProtocolException();
    }

    private static string Required(ChatStompFrame frame, string header)
    {
        return frame.Headers.TryGetValue(header, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new ChatStompProtocolException();
    }

    private async Task SendErrorAndCloseAsync(WebSocket socket, ChatRealtimeConnection? connection, Exception exception)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return;
            }
            var body = exception switch
            {
                ChatReadException access => CreateErrorBody("CHAT_WEBSOCKET_UNAUTHORIZED", access.Message),
                ChatStompProtocolException => CreateErrorBody("CHAT_WEBSOCKET_UNAUTHORIZED", "WebSocket request rejected."),
                _ => CreateErrorBody("CHAT_WEBSOCKET_INTERNAL_ERROR", "WebSocket 메시지 처리 중 오류가 발생했습니다.")
            };
            if (connection is not null)
            {
                connection.StopPublishing();
                await connection.SendAsync("ERROR", new Dictionary<string, string> { ["message"] = "CHAT_WEBSOCKET_REQUEST_REJECTED", ["content-type"] = "application/json" }, body, timeout.Token);
                await connection.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "CHAT_WEBSOCKET_REQUEST_REJECTED", timeout.Token);
            }
            else
            {
                await socket.SendAsync(ChatStompEncoder.Encode("ERROR", new Dictionary<string, string> { ["message"] = "CHAT_WEBSOCKET_REQUEST_REJECTED", ["content-type"] = "application/json" }, body).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
                await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "CHAT_WEBSOCKET_REQUEST_REJECTED", timeout.Token);
            }
        }
        catch (Exception transportException) when (transportException is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            socket.Abort();
        }
    }

    private string CreateErrorBody(string errorCode, string message)
    {
        return JsonSerializer.Serialize(new
        {
            eventType = "chat.error",
            errorCode,
            message,
            occurredAt = timeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture)
        });
    }

    private static bool IsJavaBlank(string content)
    {
        // 현재 BE Hibernate Validator 8.0.3의 @NotBlank는 Java String.trim() (<= U+0020)을 사용한다.
        foreach (var value in content)
        {
            if (value > '\u0020')
            {
                return false;
            }
        }
        return true;
    }
}
