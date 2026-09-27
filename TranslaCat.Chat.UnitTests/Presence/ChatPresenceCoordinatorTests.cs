using TranslaCat.Chat.Application.Presence;

namespace TranslaCat.Chat.UnitTests.Presence;

public sealed class ChatPresenceCoordinatorTests
{
    private static readonly DateTime EventTime = new(2026, 9, 26, 16, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public void DefaultOptions_PreserveOriginalIntervalsAndDisabledMode()
    {
        // 준비 / 실행
        var options = new ChatPresenceOptions();
        options.Validate();

        // 검증
        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(60), options.SessionTtl);
        Assert.Equal(TimeSpan.FromSeconds(20), options.RefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), options.OfflineGrace);
        Assert.Equal(TimeSpan.FromSeconds(110), options.TransitionStateTtl);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(10, 0, 0)]
    [InlineData(10, 10, 0)]
    [InlineData(10, 11, 0)]
    [InlineData(10, 1, -1)]
    public void InvalidOptions_AreRejected(int ttl, int refresh, int grace)
    {
        // 준비
        var options = new ChatPresenceOptions
        {
            SessionTtl = TimeSpan.FromMilliseconds(ttl),
            RefreshInterval = TimeSpan.FromMilliseconds(refresh),
            OfflineGrace = TimeSpan.FromMilliseconds(grace)
        };

        // 실행 / 검증
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public async Task DisabledPresence_DoesNotRegisterOrQueryStore()
    {
        // 준비
        var fixture = new Fixture(new ChatPresenceOptions());

        // 실행
        await fixture.Service.ConnectedAsync(10, "socket-a");
        var online = await fixture.Service.ResolveOnlineAsync(10);

        // 검증
        Assert.Null(online);
        Assert.Empty(fixture.Store.Calls);
        Assert.Equal(0, fixture.Service.LocalSessionCount);
    }

    [Fact]
    public async Task Connected_RegistersAndPublishesOnlyClaimedTransitionWithInjectedTime()
    {
        // 준비
        var fixture = new Fixture();
        fixture.Store.OnlineClaims.Enqueue(true);
        fixture.Store.OnlineClaims.Enqueue(false);

        // 실행
        await fixture.Service.ConnectedAsync(10, "socket-a");
        await fixture.Service.ConnectedAsync(10, "socket-a");

        // 검증
        Assert.Equal(1, fixture.Service.LocalSessionCount);
        Assert.Equal(new ChatPresenceChanged(10, true, EventTime), Assert.Single(fixture.Publisher.Events));
        Assert.Equal(["register:socket-a", "online:10", "register:socket-a", "online:10"], fixture.Store.Calls);
    }

    [Fact]
    public async Task SessionCannotBeReassignedToAnotherUser()
    {
        // 준비
        var fixture = new Fixture();
        await fixture.Service.ConnectedAsync(10, "socket-a");

        // 실행 / 검증
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.ConnectedAsync(20, "socket-a"));
        Assert.Equal(1, fixture.Service.LocalSessionCount);
        Assert.Single(fixture.Store.Calls, call => call.StartsWith("register:"));
    }

    [Fact]
    public async Task LastDisconnect_WaitsForGraceAndUsesAtomicOfflineClaim()
    {
        // 준비
        var fixture = new Fixture();
        fixture.Store.OfflineClaims.Enqueue(true);
        await fixture.Service.ConnectedAsync(10, "socket-a");
        await fixture.Service.DisconnectedAsync("socket-a");

        // 실행: deadline 직전에는 전이가 없고, 경과 뒤 한 번만 판정한다.
        fixture.Clock.Advance(TimeSpan.FromSeconds(29));
        await fixture.Service.VerifyDueOfflineAsync();
        Assert.DoesNotContain("offline:10", fixture.Store.Calls);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Service.VerifyDueOfflineAsync();
        await fixture.Service.VerifyDueOfflineAsync();

        // 검증
        Assert.Equal(new ChatPresenceChanged(10, false, EventTime), Assert.Single(fixture.Publisher.Events));
        Assert.Single(fixture.Store.Calls, call => call == "offline:10");
    }

    [Fact]
    public async Task ZeroGrace_VerifiesImmediately()
    {
        // 준비
        var fixture = new Fixture(new ChatPresenceOptions { Enabled = true, OfflineGrace = TimeSpan.Zero });
        fixture.Store.OfflineClaims.Enqueue(true);
        await fixture.Service.ConnectedAsync(10, "socket-a");

        // 실행
        await fixture.Service.DisconnectedAsync("socket-a");

        // 검증
        Assert.False(Assert.Single(fixture.Publisher.Events).Online);
    }

    [Fact]
    public async Task Reconnect_CancelsPendingGrace()
    {
        // 준비
        var fixture = new Fixture();
        fixture.Store.OfflineClaims.Enqueue(true);
        await fixture.Service.ConnectedAsync(10, "socket-a");
        await fixture.Service.DisconnectedAsync("socket-a");

        // 실행
        await fixture.Service.ConnectedAsync(10, "socket-b");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Service.VerifyDueOfflineAsync();

        // 검증
        Assert.DoesNotContain("offline:10", fixture.Store.Calls);
        Assert.Empty(fixture.Publisher.Events);
    }

    [Fact]
    public async Task OtherInstanceOnline_RejectsPendingOfflineClaim()
    {
        // 준비
        var fixture = new Fixture();
        fixture.Store.OfflineClaims.Enqueue(false);
        await fixture.Service.ConnectedAsync(10, "socket-a");
        await fixture.Service.DisconnectedAsync("socket-a");

        // 실행
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.Service.VerifyDueOfflineAsync();

        // 검증
        Assert.Contains("offline:10", fixture.Store.Calls);
        Assert.Empty(fixture.Publisher.Events);
    }

    [Fact]
    public async Task SiblingSessionRemaining_DoesNotScheduleOffline()
    {
        // 준비
        var fixture = new Fixture();
        fixture.Store.RemainingSessions = 1;
        await fixture.Service.ConnectedAsync(10, "socket-a");

        // 실행
        await fixture.Service.DisconnectedAsync("socket-a");
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.Service.VerifyDueOfflineAsync();

        // 검증
        Assert.DoesNotContain("offline:10", fixture.Store.Calls);
    }

    [Fact]
    public async Task MissingLease_IsRestoredOnlyForLiveLocalSession()
    {
        // 준비
        var fixture = new Fixture();
        fixture.Store.RefreshResult = false;
        await fixture.Service.ConnectedAsync(10, "socket-a");
        fixture.Store.OnlineClaims.Enqueue(true);
        fixture.Store.Calls.Clear();

        // 실행
        await fixture.Service.RefreshLocalSessionsAsync();

        // 검증
        Assert.Equal(["refresh:socket-a", "register:socket-a", "online:10"], fixture.Store.Calls);
        Assert.True(Assert.Single(fixture.Publisher.Events).Online);
    }

    [Fact]
    public async Task RefreshAndDisconnectRace_LastOperationRemovesLease()
    {
        // 준비: refresh 결과가 오기 전에 disconnect가 local registry를 제거한다.
        var fixture = new Fixture();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.RefreshAction = async () =>
        {
            refreshStarted.SetResult();
            return await releaseRefresh.Task;
        };
        await fixture.Service.ConnectedAsync(10, "socket-a");
        fixture.Store.Calls.Clear();

        // 실행
        var refresh = fixture.Service.RefreshLocalSessionsAsync();
        await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var disconnect = fixture.Service.DisconnectedAsync("socket-a");
        releaseRefresh.SetResult(false);
        await Task.WhenAll(refresh, disconnect);
        await fixture.Service.RefreshLocalSessionsAsync();

        // 검증: 늦은 register가 실행되어도 동일 gate 뒤 remove가 마지막이다.
        Assert.Equal("remove:socket-a", fixture.Store.Calls.Last());
        Assert.Equal(0, fixture.Service.LocalSessionCount);
        Assert.Single(fixture.Store.Calls, call => call == "remove:socket-a");
    }

    [Fact]
    public async Task RedisFailure_DoesNotDiscardLocalConnectionAndQueryIsUnknown()
    {
        // 준비
        var fixture = new Fixture();
        fixture.Store.Failure = new InvalidOperationException("synthetic dependency unavailable");

        // 실행
        await fixture.Service.ConnectedAsync(10, "socket-a");
        var online = await fixture.Service.ResolveOnlineAsync(10);
        await fixture.Service.DisconnectedAsync("socket-a");
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.Service.VerifyDueOfflineAsync();

        // 검증
        Assert.Null(online);
        Assert.Equal(["connect", "query", "disconnect", "offline"], fixture.Failures);
        Assert.Empty(fixture.Publisher.Events);
        Assert.Equal(0, fixture.Service.LocalSessionCount);
    }

    [Fact]
    public async Task BatchQueryFailure_DiscardsPartialSnapshot()
    {
        // 준비
        var fixture = new Fixture();
        fixture.Store.CountAction = id => id == 20 ? throw new InvalidOperationException("synthetic failure") : 1;

        // 실행
        var result = await fixture.Service.ResolveOnlineByUserIdsAsync([10, 20]);

        // 검증
        Assert.Empty(result);
        Assert.Equal("query-batch", Assert.Single(fixture.Failures));
    }

    [Fact]
    public async Task CancelledDisconnect_StillRemovesItsLocalAndRedisLease()
    {
        // 준비
        var fixture = new Fixture();
        await fixture.Service.ConnectedAsync(10, "socket-a");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // 실행 / 검증
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.DisconnectedAsync("socket-a", cancellation.Token));
        Assert.Contains("remove:socket-a", fixture.Store.Calls);
        Assert.Equal(0, fixture.Service.LocalSessionCount);
    }

    [Fact]
    public async Task CancelledConnect_DoesNotRegisterAnything()
    {
        // 준비
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // 실행 / 검증
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Service.ConnectedAsync(10, "socket-a", cancellation.Token));
        Assert.Empty(fixture.Store.Calls);
        Assert.Equal(0, fixture.Service.LocalSessionCount);
    }

    private sealed class Fixture
    {
        public ScriptedStore Store { get; } = new();
        public RecordingPublisher Publisher { get; } = new();
        public ManualClock Clock { get; } = new();
        public List<string> Failures { get; } = [];
        public ChatPresenceCoordinator Service
        {
            get;
        }

        public Fixture(ChatPresenceOptions? options = null)
        {
            Service = new(Store, Publisher, options ?? new()
            {
                Enabled = true
            }, Clock,
                () => EventTime, (operation, _) => Failures.Add(operation));
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 26, 16, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }

        public void Advance(TimeSpan duration)
        {
            now += duration;
        }
    }

    // 이 대역은 결과와 호출 순서만 제어한다. Redis TTL/원자성 정책을 재구현하지 않는다.
    private sealed class ScriptedStore : IChatPresenceStore
    {
        public List<string> Calls { get; } = [];
        public Queue<bool> OnlineClaims { get; } = new();
        public Queue<bool> OfflineClaims { get; } = new();
        public long RemainingSessions
        {
            get; set;
        }
        public bool RefreshResult { get; set; } = true;
        public Func<Task<bool>>? RefreshAction
        {
            get; set;
        }
        public Func<long, long>? CountAction
        {
            get; set;
        }
        public Exception? Failure
        {
            get; set;
        }

        public Task<long> RegisterSessionAsync(long userId, string sessionId, CancellationToken cancellationToken)
        {
            Record("register:" + sessionId, cancellationToken);
            return Task.FromResult(1L);
        }

        public Task<bool> RefreshSessionAsync(long userId, string sessionId, CancellationToken cancellationToken)
        {
            Record("refresh:" + sessionId, cancellationToken);
            return RefreshAction?.Invoke() ?? Task.FromResult(RefreshResult);
        }

        public Task<long> RemoveSessionAsync(long userId, string sessionId, CancellationToken cancellationToken)
        {
            Record("remove:" + sessionId, cancellationToken);
            return Task.FromResult(RemainingSessions);
        }

        public Task<long> GetActiveSessionCountAsync(long userId, CancellationToken cancellationToken)
        {
            Record("count:" + userId, cancellationToken);
            return Task.FromResult(CountAction?.Invoke(userId) ?? 0);
        }

        public Task<bool> ClaimOnlineTransitionAsync(long userId, CancellationToken cancellationToken)
        {
            Record("online:" + userId, cancellationToken);
            return Task.FromResult(OnlineClaims.TryDequeue(out var result) && result);
        }

        public Task<bool> ClaimOfflineTransitionAsync(long userId, CancellationToken cancellationToken)
        {
            Record("offline:" + userId, cancellationToken);
            return Task.FromResult(OfflineClaims.TryDequeue(out var result) && result);
        }

        private void Record(string call, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(call);
            if (Failure is not null)
            {
                throw Failure;
            }
        }
    }

    private sealed class RecordingPublisher : IChatPresencePublisher
    {
        public List<ChatPresenceChanged> Events { get; } = [];

        public Task PublishAsync(ChatPresenceChanged change, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(change);
            return Task.CompletedTask;
        }
    }
}
