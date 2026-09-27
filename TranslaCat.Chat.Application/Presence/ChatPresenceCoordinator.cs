using System.Collections.Concurrent;

namespace TranslaCat.Chat.Application.Presence;

public sealed class ChatPresenceCoordinator
{
    private readonly IChatPresenceStore store;
    private readonly IChatPresencePublisher publisher;
    private readonly ChatPresenceOptions options;
    private readonly TimeProvider timeProvider;
    private readonly Func<DateTime> eventTime;
    private readonly Action<string, Exception> reportFailure;
    private readonly ConcurrentDictionary<string, LocalSession> sessions = new();
    private readonly ConcurrentDictionary<long, PendingOffline> pendingOffline = new();

    public ChatPresenceCoordinator(
        IChatPresenceStore store,
        IChatPresencePublisher publisher,
        ChatPresenceOptions options,
        TimeProvider timeProvider,
        Func<DateTime> eventTime,
        Action<string, Exception> reportFailure)
    {
        options.Validate();
        this.store = store;
        this.publisher = publisher;
        this.options = options;
        this.timeProvider = timeProvider;
        this.eventTime = eventTime;
        this.reportFailure = reportFailure;
    }

    public int LocalSessionCount => sessions.Count;

    public async Task ConnectedAsync(long userId, string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.Enabled)
        {
            return;
        }

        // 이 registry에는 인증을 마치고 실제 연결된 local socket만 등록한다.
        var session = sessions.GetOrAdd(sessionId, _ => new LocalSession(userId, sessionId));
        if (session.UserId != userId)
        {
            throw new InvalidOperationException("A local session cannot belong to two users.");
        }

        pendingOffline.TryRemove(userId, out _);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!IsCurrent(session))
            {
                return;
            }

            await store.RegisterSessionAsync(userId, sessionId, cancellationToken);
            if (await store.ClaimOnlineTransitionAsync(userId, cancellationToken))
            {
                await publisher.PublishAsync(new(userId, true, eventTime()), cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Presence 장애는 socket 자체를 끊지 않는다. 다음 refresh가 lease를 복구한다.
            reportFailure("connect", exception);
        }
        finally
        {
            session.Gate.Release();
        }
    }

    public async Task DisconnectedAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        // refresh와 같은 session gate를 사용하여 disconnect 뒤 늦은 재등록을 막는다.
        // local 제거 이후에는 caller 취소 여부와 무관하게 best-effort lease 정리를 완료한다.
        await session.Gate.WaitAsync(CancellationToken.None);
        try
        {
            if (await store.RemoveSessionAsync(session.UserId, session.SessionId, CancellationToken.None) == 0)
            {
                ScheduleOffline(session.UserId);
            }
        }
        catch (Exception exception)
        {
            reportFailure("disconnect", exception);
            ScheduleOffline(session.UserId);
        }
        finally
        {
            session.Gate.Release();
        }

        if (options.OfflineGrace == TimeSpan.Zero)
        {
            await VerifyDueOfflineAsync(CancellationToken.None);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task RefreshLocalSessionsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var session in sessions.Values.ToArray())
        {
            await session.Gate.WaitAsync(cancellationToken);
            try
            {
                // snapshot을 얻은 뒤 disconnect된 session은 lease를 되살리지 않는다.
                if (!IsCurrent(session))
                {
                    continue;
                }

                if (await store.RefreshSessionAsync(session.UserId, session.SessionId, cancellationToken))
                {
                    continue;
                }

                await store.RegisterSessionAsync(session.UserId, session.SessionId, cancellationToken);
                if (await store.ClaimOnlineTransitionAsync(session.UserId, cancellationToken))
                {
                    await publisher.PublishAsync(new(session.UserId, true, eventTime()), cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                reportFailure("refresh", exception);
            }
            finally
            {
                session.Gate.Release();
            }
        }
    }

    public async Task VerifyDueOfflineAsync(CancellationToken cancellationToken = default)
    {
        foreach (var pair in pendingOffline.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pair.Value.DueAt > timeProvider.GetUtcNow() || !pendingOffline.TryRemove(pair))
            {
                continue;
            }

            // local 재접속은 빠르게 걸러내고 다른 instance 재접속은 저장소가 최종 판정한다.
            if (sessions.Values.Any(session => session.UserId == pair.Key))
            {
                continue;
            }

            try
            {
                if (await store.ClaimOfflineTransitionAsync(pair.Key, cancellationToken))
                {
                    await publisher.PublishAsync(new(pair.Key, false, eventTime()), cancellationToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 원본과 같이 실패한 grace 검증을 ONLINE/OFFLINE 성공으로 만들지 않는다.
                reportFailure("offline", exception);
            }
        }
    }

    public async Task<bool?> ResolveOnlineAsync(long? userId, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || userId is null or <= 0)
        {
            return null;
        }

        try
        {
            return await store.GetActiveSessionCountAsync(userId.Value, cancellationToken) > 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            reportFailure("query", exception);
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<long, bool>> ResolveOnlineByUserIdsAsync(
        IEnumerable<long> userIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<long, bool>();
        if (!options.Enabled)
        {
            return result;
        }

        try
        {
            foreach (var userId in userIds.Where(id => id > 0).Distinct())
            {
                result[userId] = await store.GetActiveSessionCountAsync(userId, cancellationToken) > 0;
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 조회 중 한 번이라도 실패하면 부분적인 ONLINE 목록을 신뢰하지 않는다.
            reportFailure("query-batch", exception);
            return new Dictionary<long, bool>();
        }
    }

    private bool IsCurrent(LocalSession session)
    {
        return sessions.TryGetValue(session.SessionId, out var current) && ReferenceEquals(current, session);
    }

    private void ScheduleOffline(long userId)
    {
        pendingOffline[userId] = new(Guid.NewGuid(), timeProvider.GetUtcNow() + options.OfflineGrace);
    }

    private sealed record PendingOffline(Guid Token, DateTimeOffset DueAt);

    private sealed class LocalSession(long userId, string sessionId)
    {
        public long UserId { get; } = userId;
        public string SessionId { get; } = sessionId;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
