using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using TranslaCat.Chat.Api.Presence;
using TranslaCat.Chat.Application.Presence;
using TranslaCat.Chat.Infrastructure.Redis;

namespace TranslaCat.Chat.IntegrationTests;

[Collection("Chat Redis exclusive runtime")]
public sealed class RedisPresenceTests
{
    [Fact]
    public async Task NativeTtl_ExpiresLostDisconnectWithoutRevivingOnLateRefresh()
    {
        // 준비: 두 client는 같은 전용 Redis를 사용하되 실행별 namespace를 새로 쓴다.
        await using var fixture = await RedisFixture.CreateAsync();
        await fixture.First.RegisterSessionAsync(100, "lost-socket");
        var ttl = await fixture.FirstConnection.GetDatabase().KeyTimeToLiveAsync(fixture.Key(100, "session:lost-socket"));

        // 실행
        await UntilAsync(async () => await fixture.Second.GetActiveSessionCountAsync(100) == 0);
        var refreshed = await fixture.Second.RefreshSessionAsync(100, "lost-socket");

        // 검증: native TTL과 index 만료가 실제 Redis에서 적용되었다.
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value.TotalMilliseconds, 1, fixture.Options.SessionTtl.TotalMilliseconds);
        Assert.False(refreshed);
        Assert.False(await fixture.FirstConnection.GetDatabase().KeyExistsAsync(fixture.Key(100, "session:lost-socket")));
    }

    [Fact]
    public async Task Refresh_ExtendsOnlyLiveSiblingAndPreservesTransitionTtl()
    {
        // 준비
        await using var fixture = await RedisFixture.CreateAsync();
        await fixture.First.RegisterSessionAsync(200, "expired");
        await fixture.Second.RegisterSessionAsync(200, "live");
        Assert.True(await fixture.First.ClaimOnlineTransitionAsync(200));
        await Task.Delay(650);

        // 실행
        Assert.True(await fixture.Second.RefreshSessionAsync(200, "live"));
        await UntilAsync(async () => !await fixture.FirstConnection.GetDatabase().KeyExistsAsync(fixture.Key(200, "session:expired")));

        // 검증
        Assert.Equal(1, await fixture.First.GetActiveSessionCountAsync(200));
        Assert.False(await fixture.First.RefreshSessionAsync(200, "expired"));
        Assert.Equal("ONLINE", (string?)await fixture.SecondConnection.GetDatabase().StringGetAsync(fixture.Key(200, "state")));
        Assert.False(await fixture.Second.ClaimOnlineTransitionAsync(200));
    }

    [Fact]
    public async Task DuplicateRegister_IsIdempotentAndPreservesInt64Identity()
    {
        // 준비
        await using var fixture = await RedisFixture.CreateAsync();
        const long userId = long.MaxValue;

        // 실행
        var results = await Task.WhenAll(
            fixture.First.RegisterSessionAsync(userId, "same"),
            fixture.Second.RegisterSessionAsync(userId, "same"));

        // 검증
        Assert.All(results, count => Assert.Equal(1, count));
        Assert.Equal(1, await fixture.Second.GetActiveSessionCountAsync(userId));
        Assert.Equal(userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            (string?)await fixture.FirstConnection.GetDatabase().StringGetAsync(fixture.Key(userId, "session:same")));
    }

    [Fact]
    public async Task ConcurrentOnlineClaims_AcrossIndependentConnections_HaveOneWinner()
    {
        // 준비
        await using var fixture = await RedisFixture.CreateAsync();
        await fixture.First.RegisterSessionAsync(300, "socket");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = Enumerable.Range(0, 20).Select(async index =>
        {
            await start.Task;
            return await (index % 2 == 0 ? fixture.First : fixture.Second).ClaimOnlineTransitionAsync(300);
        }).ToArray();

        // 실행
        start.SetResult();
        var results = await Task.WhenAll(claims);

        // 검증: process lock 없이 Lua가 중복 ONLINE 판정을 막는다.
        Assert.Single(results, result => result);
    }

    [Fact]
    public async Task OtherInstanceSession_BlocksOfflineUntilBothDisconnect()
    {
        // 준비
        await using var fixture = await RedisFixture.CreateAsync();
        await fixture.First.RegisterSessionAsync(400, "instance-a");
        await fixture.Second.RegisterSessionAsync(400, "instance-b");
        await fixture.First.ClaimOnlineTransitionAsync(400);

        // 실행 / 검증: 한 연결만 종료해도 다른 instance의 세션은 유지된다.
        Assert.Equal(1, await fixture.First.RemoveSessionAsync(400, "instance-a"));
        Assert.False(await fixture.First.ClaimOfflineTransitionAsync(400));
        Assert.Equal(1, await fixture.Second.GetActiveSessionCountAsync(400));
        Assert.Equal(0, await fixture.Second.RemoveSessionAsync(400, "instance-b"));

        var claims = await Task.WhenAll(
            fixture.First.ClaimOfflineTransitionAsync(400),
            fixture.Second.ClaimOfflineTransitionAsync(400));
        Assert.Single(claims, result => result);

        // 실행 / 검증: OFFLINE 뒤 새 연결은 다시 한 번 ONLINE을 획득한다.
        await fixture.Second.RegisterSessionAsync(400, "reconnected");
        Assert.True(await fixture.First.ClaimOnlineTransitionAsync(400));
        Assert.False(await fixture.Second.ClaimOnlineTransitionAsync(400));
    }

    [Fact]
    public async Task Remove_ThenLateRefresh_DoesNotResurrectSession()
    {
        // 준비
        await using var fixture = await RedisFixture.CreateAsync();
        await fixture.First.RegisterSessionAsync(500, "gone");

        // 실행
        Assert.Equal(0, await fixture.Second.RemoveSessionAsync(500, "gone"));
        var refreshed = await fixture.First.RefreshSessionAsync(500, "gone");

        // 검증
        Assert.False(refreshed);
        Assert.Equal(0, await fixture.Second.GetActiveSessionCountAsync(500));
    }

    [Fact]
    public async Task Namespace_IsolatesStateAndPubSubAcrossSameRedisDatabase()
    {
        // 준비
        await using var fixture = await RedisFixture.CreateAsync();
        var otherPrefix = fixture.Prefix + ":other";
        var otherStore = new RedisChatPresenceStore(fixture.SecondConnection, fixture.Options, otherPrefix, TimeProvider.System);
        var bus = new RedisChatEventBus(fixture.FirstConnection, fixture.Prefix);
        var otherBus = new RedisChatEventBus(fixture.SecondConnection, otherPrefix);
        var received = new ConcurrentQueue<string>();
        await using var subscription = await otherBus.SubscribeAsync("events", payload =>
        {
            received.Enqueue(payload);
            return Task.CompletedTask;
        }, exception => throw new InvalidOperationException("Unexpected consumer failure.", exception));

        // 실행
        await fixture.First.RegisterSessionAsync(600, "private-to-scope");
        var crossScopeCount = await bus.PublishAsync("events", "wrong-scope");
        await otherBus.PublishAsync("events", "own-scope");
        await UntilAsync(() => Task.FromResult(!received.IsEmpty));

        // 검증
        Assert.Equal(0, await otherStore.GetActiveSessionCountAsync(600));
        Assert.Equal(0, crossScopeCount);
        Assert.Equal("own-scope", Assert.Single(received));
    }

    [Fact]
    public async Task PubSub_DeliversToTwoAppClientsAndSurvivesMalformedPayload()
    {
        // 준비
        await using var fixture = await RedisFixture.CreateAsync();
        var firstEvents = new ConcurrentQueue<ChatPresenceChanged>();
        var secondEvents = new ConcurrentQueue<ChatPresenceChanged>();
        var errors = new ConcurrentQueue<string>();
        var firstBus = new RedisChatEventBus(fixture.FirstConnection, fixture.Prefix);
        var secondBus = new RedisChatEventBus(fixture.SecondConnection, fixture.Prefix);
        await using var firstPublisher = CreatePublisher(firstBus, firstEvents, errors);
        await using var secondPublisher = CreatePublisher(secondBus, secondEvents, errors);
        await firstPublisher.StartAsync();
        await secondPublisher.StartAsync();

        // 실행: 잘못된 수신 이후에도 정상 payload의 fan-out이 계속된다.
        await firstBus.PublishAsync("presence:events", "not-json");
        var change = new ChatPresenceChanged(700, true, new DateTime(2026, 9, 26, 15, 0, 0));
        await firstPublisher.PublishAsync(change);
        await UntilAsync(() => Task.FromResult(firstEvents.Count == 1 && secondEvents.Count == 1 && errors.Count >= 2));

        // 검증
        Assert.Equal(change, Assert.Single(firstEvents));
        Assert.Equal(change, Assert.Single(secondEvents));
        Assert.All(errors, operation => Assert.Equal("presence-consume", operation));
    }

    [Fact]
    public async Task PublisherWithoutLocalSubscription_FansOutLocallyAndRemotely()
    {
        // 준비
        await using var fixture = await RedisFixture.CreateAsync();
        var local = new ConcurrentQueue<ChatPresenceChanged>();
        var remote = new ConcurrentQueue<ChatPresenceChanged>();
        var errors = new ConcurrentQueue<string>();
        await using var firstPublisher = CreatePublisher(new(fixture.FirstConnection, fixture.Prefix), local, errors);
        await using var secondPublisher = CreatePublisher(new(fixture.SecondConnection, fixture.Prefix), remote, errors);
        await secondPublisher.StartAsync();
        var change = new ChatPresenceChanged(701, false, new DateTime(2026, 9, 26, 15, 0, 0));

        // 실행
        await firstPublisher.PublishAsync(change);
        await UntilAsync(() => Task.FromResult(remote.Count == 1));

        // 검증
        Assert.Equal(change, Assert.Single(local));
        Assert.Equal(change, Assert.Single(remote));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task TwoCoordinators_ReconnectDuringGraceSuppressesOffline()
    {
        // 준비
        await using var fixture = await RedisFixture.CreateAsync();
        var events = new RecordingPublisher();
        var first = CreateCoordinator(fixture.First, events, fixture.Options);
        var second = CreateCoordinator(fixture.Second, events, fixture.Options);
        await first.ConnectedAsync(800, "old");
        await first.DisconnectedAsync("old");

        // 실행
        await second.ConnectedAsync(800, "new");
        await Task.Delay(fixture.Options.OfflineGrace + TimeSpan.FromMilliseconds(20));
        await first.VerifyDueOfflineAsync();

        // 검증
        Assert.True(Assert.Single(events.Events).Online);
        Assert.Equal(1, await fixture.First.GetActiveSessionCountAsync(800));

        // 실행 / 검증: 새 연결도 종료된 후 grace가 지나야 OFFLINE이다.
        await second.DisconnectedAsync("new");
        await Task.Delay(fixture.Options.OfflineGrace + TimeSpan.FromMilliseconds(20));
        await Task.WhenAll(first.VerifyDueOfflineAsync(), second.VerifyDueOfflineAsync());
        Assert.Equal([true, false], events.Events.Select(change => change.Online));
    }

    [Fact]
    public async Task AliveLocalConnection_RecoversAfterLeaseAndTransitionExpire()
    {
        // 준비
        await using var fixture = await RedisFixture.CreateAsync();
        var events = new RecordingPublisher();
        var coordinator = CreateCoordinator(fixture.First, events, fixture.Options);
        await coordinator.ConnectedAsync(900, "still-physically-open");

        // 실행: 삭제/flush 없이 native TTL 소실 후 live local 연결로 복구한다.
        await UntilAsync(async () => !await fixture.SecondConnection.GetDatabase().KeyExistsAsync(fixture.Key(900, "state")));
        await coordinator.RefreshLocalSessionsAsync();

        // 검증
        Assert.Equal(1, await fixture.Second.GetActiveSessionCountAsync(900));
        Assert.Equal(2, events.Events.Count);
        Assert.All(events.Events, change => Assert.True(change.Online));
    }

    [Fact]
    public async Task CancelledOperation_DoesNotWriteAnySession()
    {
        // 준비
        await using var fixture = await RedisFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // 실행 / 검증
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.First.RegisterSessionAsync(1000, "cancelled", cancellation.Token));
        Assert.Equal(0, await fixture.Second.GetActiveSessionCountAsync(1000));
    }

    [Fact]
    public async Task ClosedClient_ReportsUnknownAndUsesLocalPublishFallback()
    {
        // 준비: 서버를 중지하지 않고 이번 테스트가 소유한 별도 client만 종료한다.
        await using var fixture = await RedisFixture.CreateAsync();
        using var closedClient = await ConnectionMultiplexer.ConnectAsync(fixture.FirstConnection.Configuration);
        var store = new RedisChatPresenceStore(closedClient, fixture.Options, fixture.Prefix, TimeProvider.System);
        var errors = new ConcurrentQueue<string>();
        var localEvents = new ConcurrentQueue<ChatPresenceChanged>();
        await using var publisher = CreatePublisher(new(closedClient, fixture.Prefix), localEvents, errors);
        var coordinator = new ChatPresenceCoordinator(store, publisher, fixture.Options, TimeProvider.System,
            () => new DateTime(2026, 9, 26, 15, 0, 0), (operation, _) => errors.Enqueue(operation));
        closedClient.Dispose();

        // 실행
        var online = await coordinator.ResolveOnlineAsync(1100);
        await publisher.PublishAsync(new(1100, true, new DateTime(2026, 9, 26, 15, 0, 0)));

        // 검증: client 장애 대역이 아닌 실제 종료 client이며 서버 재시작 시험과는 구분한다.
        Assert.Null(online);
        Assert.Equal(["query", "presence-publish"], errors);
        Assert.Single(localEvents);
        Assert.Equal(0, await fixture.Second.GetActiveSessionCountAsync(1100));
    }

    [Fact]
    public async Task HostedWorker_RefreshesLiveLeaseThenStopsAndUnsubscribes()
    {
        // 준비: worker/Redis는 실제이며 socket 생존 정보만 coordinator 호출로 제공한다.
        await using var fixture = await RedisFixture.CreateAsync();
        var received = new ConcurrentQueue<ChatPresenceChanged>();
        var errors = new ConcurrentQueue<string>();
        await using var publisher = CreatePublisher(new(fixture.FirstConnection, fixture.Prefix), received, errors);
        var coordinator = CreateCoordinator(fixture.First, publisher, fixture.Options);
        using var worker = new ChatPresenceHostedService(coordinator, publisher, fixture.Options,
            TimeProvider.System, NullLogger<ChatPresenceHostedService>.Instance);
        await worker.StartAsync(CancellationToken.None);

        try
        {
            await UntilAsync(() => Task.FromResult(publisher.IsSubscribed));
            await coordinator.ConnectedAsync(1200, "worker-owned-socket");

            // 실행: 최초 TTL보다 오래 기다려 실제 fixed-delay refresh를 확인한다.
            await Task.Delay(fixture.Options.SessionTtl + TimeSpan.FromMilliseconds(300));
            Assert.Equal(1, await fixture.Second.GetActiveSessionCountAsync(1200));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // 검증: worker 정지 뒤에는 더 이상 refresh하지 않으므로 lease가 자연 만료한다.
        Assert.False(publisher.IsSubscribed);
        await UntilAsync(async () => await fixture.Second.GetActiveSessionCountAsync(1200) == 0);
        Assert.Empty(errors);
        Assert.True(Assert.Single(received).Online);
    }

    [Fact]
    public async Task TwoHostedWorkers_ShareTransitionClaimsWithoutDuplicateFanout()
    {
        // 준비
        await using var fixture = await RedisFixture.CreateAsync();
        var firstEvents = new ConcurrentQueue<ChatPresenceChanged>();
        var secondEvents = new ConcurrentQueue<ChatPresenceChanged>();
        var errors = new ConcurrentQueue<string>();
        await using var firstPublisher = CreatePublisher(new(fixture.FirstConnection, fixture.Prefix), firstEvents, errors);
        await using var secondPublisher = CreatePublisher(new(fixture.SecondConnection, fixture.Prefix), secondEvents, errors);
        var first = CreateCoordinator(fixture.First, firstPublisher, fixture.Options);
        var second = CreateCoordinator(fixture.Second, secondPublisher, fixture.Options);
        using var firstWorker = new ChatPresenceHostedService(first, firstPublisher, fixture.Options,
            TimeProvider.System, NullLogger<ChatPresenceHostedService>.Instance);
        using var secondWorker = new ChatPresenceHostedService(second, secondPublisher, fixture.Options,
            TimeProvider.System, NullLogger<ChatPresenceHostedService>.Instance);
        await Task.WhenAll(firstWorker.StartAsync(CancellationToken.None), secondWorker.StartAsync(CancellationToken.None));

        try
        {
            await UntilAsync(() => Task.FromResult(firstPublisher.IsSubscribed && secondPublisher.IsSubscribed));
            await Task.WhenAll(first.ConnectedAsync(1300, "first-worker"), second.ConnectedAsync(1300, "second-worker"));
            await UntilAsync(() => Task.FromResult(firstEvents.Count == 1 && secondEvents.Count == 1));

            // 실행: 두 worker의 grace 검증이 경합해도 Lua claim 승자만 OFFLINE을 발행한다.
            await Task.WhenAll(first.DisconnectedAsync("first-worker"), second.DisconnectedAsync("second-worker"));
            await UntilAsync(() => Task.FromResult(firstEvents.Count >= 2 && secondEvents.Count >= 2));

            // 검증
            Assert.Equal([true, false], firstEvents.Select(change => change.Online));
            Assert.Equal([true, false], secondEvents.Select(change => change.Online));
            Assert.Empty(errors);
        }
        finally
        {
            await Task.WhenAll(firstWorker.StopAsync(CancellationToken.None), secondWorker.StopAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task OwnedRedisRestart_ReportsOutageThenRecoversLeasesAndSubscriptions()
    {
        // 준비: 이 collection은 다른 integration collection과 병렬 실행하지 않는다.
        // manifest와 실제 label이 모두 일치하는 이번 실행의 Redis container만 제어한다.
        await using var fixture = await RedisFixture.CreateAsync();
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("CHAT_TEST_MANIFEST")!));
        var container = manifest.RootElement.GetProperty("redis").GetString()!;
        var runId = manifest.RootElement.GetProperty("runId").GetString()!;
        Assert.Matches("^[a-f0-9]{64}$", container);
        Assert.Equal(runId, (await DockerAsync("inspect", "--format", "{{index .Config.Labels \"translacat.chat.run\"}}", container)).Trim());

        var firstEvents = new ConcurrentQueue<ChatPresenceChanged>();
        var secondEvents = new ConcurrentQueue<ChatPresenceChanged>();
        var errors = new ConcurrentQueue<string>();
        await using var firstPublisher = CreatePublisher(new(fixture.FirstConnection, fixture.Prefix), firstEvents, errors);
        await using var secondPublisher = CreatePublisher(new(fixture.SecondConnection, fixture.Prefix), secondEvents, errors);
        var coordinator = new ChatPresenceCoordinator(fixture.First, firstPublisher, fixture.Options, TimeProvider.System,
            () => new DateTime(2026, 9, 27), (operation, _) => errors.Enqueue(operation));
        await Task.WhenAll(firstPublisher.StartAsync(), secondPublisher.StartAsync());
        await coordinator.ConnectedAsync(1400, "surviving-local-socket");
        await UntilAsync(() => Task.FromResult(firstEvents.Count == 1 && secondEvents.Count == 1));

        try
        {
            // 실행: Redis down 중 UNKNOWN과 local fallback을 확인한다. OFFLINE 성공으로 숨기지 않는다.
            await DockerAsync("stop", "--time", "1", container);
            Assert.Null(await coordinator.ResolveOnlineAsync(1400));
            await firstPublisher.PublishAsync(new(1401, true, new DateTime(2026, 9, 27)));
            Assert.Contains("query", errors);
            Assert.Contains("presence-publish", errors);
            Assert.Equal(2, firstEvents.Count);
            Assert.Single(secondEvents);
        }
        finally
        {
            // 검증 중 assertion이 실패해도 이번 runtime의 Redis를 원래 실행 상태로 돌린다.
            await DockerAsync("start", container);
        }

        await UntilAsync(async () =>
        {
            try
            {
                await fixture.FirstConnection.GetDatabase().PingAsync();
                await fixture.SecondConnection.GetSubscriber().PingAsync();
                return fixture.FirstConnection.GetSubscriber().IsConnected()
                    && fixture.SecondConnection.GetSubscriber().IsConnected();
            }
            catch (RedisException)
            {
                return false;
            }
        });

        // 실행 / 검증: restart 후 실제 local 생존 정보로 lease와 ONLINE 전이를 재구성한다.
        await coordinator.RefreshLocalSessionsAsync();
        await UntilAsync(() => Task.FromResult(secondEvents.Count(change => change.UserId == 1400) == 2));
        Assert.Equal(1, await fixture.Second.GetActiveSessionCountAsync(1400));
        Assert.Equal(2, secondEvents.Count(change => change.UserId == 1400));
        Assert.All(secondEvents.Where(change => change.UserId == 1400), change => Assert.True(change.Online));
    }

    [Theory]
    [InlineData("shared")]
    [InlineData("translacat:chat:")]
    [InlineData("translacat:chat:*")]
    [InlineData("translacat:chat:{global}")]
    public void InvalidScope_IsRejectedBeforeConnectionUse(string prefix)
    {
        // 준비 / 실행 / 검증
        Assert.Throws<ArgumentException>(() => RedisChatNamespace.Validate(prefix));
    }

    private static RedisChatPresencePublisher CreatePublisher(
        RedisChatEventBus bus,
        ConcurrentQueue<ChatPresenceChanged> events,
        ConcurrentQueue<string> errors)
    {
        return new(bus, change =>
        {
            events.Enqueue(change);
            return Task.CompletedTask;
        }, (operation, _) => errors.Enqueue(operation));
    }

    private static ChatPresenceCoordinator CreateCoordinator(
        IChatPresenceStore store,
        IChatPresencePublisher publisher,
        ChatPresenceOptions options)
    {
        return new(store, publisher, options, TimeProvider.System,
            () => new DateTime(2026, 9, 26, 15, 0, 0),
            (operation, exception) => throw new InvalidOperationException("Unexpected presence failure: " + operation, exception));
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!await condition())
        {
            await Task.Delay(30, timeout.Token);
        }
    }

    private static async Task<string> DockerAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Docker runtime check.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        await errors;
        Assert.Equal(0, process.ExitCode);
        return await output;
    }

    private sealed class RecordingPublisher : IChatPresencePublisher
    {
        public ConcurrentQueue<ChatPresenceChanged> Events { get; } = new();

        public Task PublishAsync(ChatPresenceChanged change, CancellationToken cancellationToken)
        {
            Events.Enqueue(change);
            return Task.CompletedTask;
        }
    }

    private sealed class RedisFixture : IAsyncDisposable
    {
        public ConnectionMultiplexer FirstConnection
        {
            get;
        }
        public ConnectionMultiplexer SecondConnection
        {
            get;
        }
        public RedisChatPresenceStore First
        {
            get;
        }
        public RedisChatPresenceStore Second
        {
            get;
        }
        public string Prefix
        {
            get;
        }
        public ChatPresenceOptions Options
        {
            get;
        } = new()
        {
            Enabled = true,
            SessionTtl = TimeSpan.FromMilliseconds(1200),
            RefreshInterval = TimeSpan.FromMilliseconds(200),
            OfflineGrace = TimeSpan.FromMilliseconds(200)
        };

        private RedisFixture(ConnectionMultiplexer first, ConnectionMultiplexer second, string runId)
        {
            FirstConnection = first;
            SecondConnection = second;
            Prefix = "translacat:chat:test:" + runId + ":" + Guid.NewGuid().ToString("N");
            First = new(first, Options, Prefix, TimeProvider.System);
            Second = new(second, Options, Prefix, TimeProvider.System);
        }

        public RedisKey Key(long userId, string suffix)
        {
            return $"{Prefix}:user:{{{userId}}}:{suffix}";
        }

        public static async Task<RedisFixture> CreateAsync()
        {
            // root 실행 스크립트가 container label을 검사한 manifest와 endpoint만 허용한다.
            var manifestPath = Environment.GetEnvironmentVariable("CHAT_TEST_MANIFEST")
                ?? throw new InvalidOperationException("Run through Invoke-ChatIntegration.ps1 with an owned runtime manifest.");
            var runId = Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID");
            var configuration = Environment.GetEnvironmentVariable("CHAT_TEST_REDIS")
                ?? throw new InvalidOperationException("CHAT_TEST_REDIS is required; integration tests never replace Redis with a fake.");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            Assert.True(manifest.RootElement.GetProperty("testMode").GetBoolean());
            Assert.Equal(manifest.RootElement.GetProperty("runId").GetString(), runId);

            var options = ConfigurationOptions.Parse(configuration);
            Assert.Single(options.EndPoints);
            var endpoint = options.EndPoints.Single();
            var port = endpoint switch
            {
                IPEndPoint ip when IPAddress.IsLoopback(ip.Address) => ip.Port,
                DnsEndPoint dns when dns.Host == "127.0.0.1" => dns.Port,
                _ => throw new InvalidOperationException("Redis integration target must be a manifest-owned loopback endpoint.")
            };
            Assert.Equal(manifest.RootElement.GetProperty("redisPort").GetInt32(), port);
            options.AbortOnConnectFail = true;

            var first = await ConnectionMultiplexer.ConnectAsync(options);
            try
            {
                var second = await ConnectionMultiplexer.ConnectAsync(options);
                return new(first, second, runId!);
            }
            catch
            {
                await first.CloseAsync();
                first.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // 서버/키 전체를 정리하지 않는다. 테스트 lease는 native TTL로 자연 만료한다.
            await Task.WhenAll(FirstConnection.CloseAsync(), SecondConnection.CloseAsync());
            FirstConnection.Dispose();
            SecondConnection.Dispose();
        }
    }
}

[CollectionDefinition("Chat Redis exclusive runtime", DisableParallelization = true)]
public sealed class RedisExclusiveRuntimeCollection;
