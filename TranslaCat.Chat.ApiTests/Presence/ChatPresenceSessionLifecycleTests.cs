using TranslaCat.Chat.Api.Presence;
using TranslaCat.Chat.Application.Presence;

namespace TranslaCat.Chat.ApiTests.Presence;

public sealed class ChatPresenceSessionLifecycleTests
{
    [Fact]
    public async Task Bridge_RegistersAuthenticatedIdentityAndDisconnectsOriginalSessionOwner()
    {
        // 준비: 이 테스트는 bridge 위임 범위만 확인한다. 실제 JWT/HTTP/Redis 검증은 별도다.
        var store = new RecordingStore();
        var coordinator = new ChatPresenceCoordinator(store, new SilentPublisher(),
            new ChatPresenceOptions { Enabled = true }, TimeProvider.System,
            () => new DateTime(2026, 9, 27), (_, exception) => throw exception);
        var lifecycle = new ChatPresenceSessionLifecycle(coordinator);

        // 실행: 종료 때 전달된 ID에 의존하지 않고 원래 등록자를 정리한다.
        await lifecycle.ConnectAsync("verified-socket", 9223372036854775807, CancellationToken.None);
        await lifecycle.DisconnectAsync("verified-socket", 10, CancellationToken.None);

        // 검증
        Assert.Equal([(long.MaxValue, "verified-socket")], store.Registered);
        Assert.Equal([(long.MaxValue, "verified-socket")], store.Removed);
        Assert.Equal(0, coordinator.LocalSessionCount);
    }

    [Fact]
    public async Task DuplicateDisconnect_IsNoOp()
    {
        // 준비
        var store = new RecordingStore();
        var coordinator = new ChatPresenceCoordinator(store, new SilentPublisher(),
            new ChatPresenceOptions { Enabled = true }, TimeProvider.System,
            () => new DateTime(2026, 9, 27), (_, exception) => throw exception);
        var lifecycle = new ChatPresenceSessionLifecycle(coordinator);
        await lifecycle.ConnectAsync("socket", 10, CancellationToken.None);

        // 실행
        await lifecycle.DisconnectAsync("socket", 10, CancellationToken.None);
        await lifecycle.DisconnectAsync("socket", 10, CancellationToken.None);

        // 검증
        Assert.Single(store.Removed);
    }

    private sealed class RecordingStore : IChatPresenceStore
    {
        public List<(long UserId, string SessionId)> Registered { get; } = [];
        public List<(long UserId, string SessionId)> Removed { get; } = [];

        public Task<long> RegisterSessionAsync(long userId, string sessionId, CancellationToken cancellationToken)
        {
            Registered.Add((userId, sessionId));
            return Task.FromResult(1L);
        }

        public Task<long> RemoveSessionAsync(long userId, string sessionId, CancellationToken cancellationToken)
        {
            Removed.Add((userId, sessionId));
            return Task.FromResult(0L);
        }

        public Task<bool> RefreshSessionAsync(long userId, string sessionId, CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task<long> GetActiveSessionCountAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult(0L);
        }

        public Task<bool> ClaimOnlineTransitionAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public Task<bool> ClaimOfflineTransitionAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class SilentPublisher : IChatPresencePublisher
    {
        public Task PublishAsync(ChatPresenceChanged change, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
