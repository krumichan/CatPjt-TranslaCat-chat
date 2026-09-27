using StackExchange.Redis;

namespace TranslaCat.Chat.Infrastructure.Redis;

public sealed class RedisChatEventBus(IConnectionMultiplexer connection, string keyPrefix)
{
    private readonly string prefix = ValidatePrefix(keyPrefix);

    public async Task<long> PublishAsync(string channel, string payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await connection.GetSubscriber().PublishAsync(ResolveChannel(channel), payload);
    }

    public async Task<IAsyncDisposable> SubscribeAsync(
        string channel,
        Func<string, Task> handler,
        Action<Exception> reportFailure,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var queue = await connection.GetSubscriber().SubscribeAsync(ResolveChannel(channel));

        // 취소가 subscription 생성과 경합하면 자신이 만든 subscription만 닫는다.
        if (cancellationToken.IsCancellationRequested)
        {
            await queue.UnsubscribeAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }

        queue.OnMessage(async message =>
        {
            try
            {
                await handler(message.Message.ToString());
            }
            catch (Exception exception)
            {
                // 잘못된 한 payload/consumer 실패가 후속 이벤트 수신을 종료하지 않게 한다.
                reportFailure(exception);
            }
        });

        return new Subscription(queue);
    }

    private RedisChannel ResolveChannel(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        return RedisChannel.Literal(prefix + ":" + channel);
    }

    private static string ValidatePrefix(string value)
    {
        RedisChatNamespace.Validate(value);
        return value;
    }

    private sealed class Subscription(ChannelMessageQueue queue) : IAsyncDisposable
    {
        private int disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                await queue.UnsubscribeAsync();
            }
        }
    }
}
