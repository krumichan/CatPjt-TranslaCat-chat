using System.Globalization;
using StackExchange.Redis;
using TranslaCat.Chat.Application.Presence;

namespace TranslaCat.Chat.Infrastructure.Redis;

public sealed class RedisChatPresenceStore : IChatPresenceStore
{
    private readonly IDatabase database;
    private readonly ChatPresenceOptions options;
    private readonly string keyPrefix;
    private readonly TimeProvider timeProvider;

    public RedisChatPresenceStore(
        IConnectionMultiplexer connection,
        ChatPresenceOptions options,
        string keyPrefix,
        TimeProvider timeProvider)
    {
        options.Validate();
        RedisChatNamespace.Validate(keyPrefix);
        database = connection.GetDatabase();
        this.options = options;
        this.keyPrefix = keyPrefix;
        this.timeProvider = timeProvider;
    }

    public async Task<long> RegisterSessionAsync(long userId, string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return (long)await EvaluateAsync(RegisterScript, SessionKeys(userId, sessionId),
            SessionArguments(userId, sessionId), cancellationToken);
    }

    public async Task<bool> RefreshSessionAsync(long userId, string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return (long)await EvaluateAsync(RefreshScript, SessionKeys(userId, sessionId),
            SessionArguments(userId, sessionId), cancellationToken) == 1;
    }

    public async Task<long> RemoveSessionAsync(long userId, string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return (long)await EvaluateAsync(RemoveScript, SessionKeys(userId, sessionId),
            [sessionId, NowMilliseconds, StateTtlMilliseconds], cancellationToken);
    }

    public async Task<long> GetActiveSessionCountAsync(long userId, CancellationToken cancellationToken = default)
    {
        return (long)await EvaluateAsync(CountScript, [UserPrefix(userId) + ":sessions"],
            [NowMilliseconds], cancellationToken);
    }

    public async Task<bool> ClaimOnlineTransitionAsync(long userId, CancellationToken cancellationToken = default)
    {
        return (long)await EvaluateAsync(ClaimOnlineScript, TransitionKeys(userId),
            [NowMilliseconds, "ONLINE", StateTtlMilliseconds], cancellationToken) == 1;
    }

    public async Task<bool> ClaimOfflineTransitionAsync(long userId, CancellationToken cancellationToken = default)
    {
        return (long)await EvaluateAsync(ClaimOfflineScript, TransitionKeys(userId),
            [NowMilliseconds, "OFFLINE", StateTtlMilliseconds], cancellationToken) == 1;
    }

    private async Task<RedisResult> EvaluateAsync(
        string script,
        RedisKey[] keys,
        RedisValue[] arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Redis command는 전송 뒤 취소할 수 없다. 결과/timeout까지 기다려 session gate를 보존한다.
        // 호출자 취소를 성공적인 Redis rollback이라고 해석하지 않는다.
        return await database.ScriptEvaluateAsync(script, keys, arguments);
    }

    private RedisValue[] SessionArguments(long userId, string sessionId)
    {
        var now = NowMilliseconds;
        var ttl = (long)options.SessionTtl.TotalMilliseconds;
        return [userId, sessionId, ttl, checked(now + ttl), now, StateTtlMilliseconds];
    }

    private RedisKey[] SessionKeys(long userId, string sessionId)
    {
        return [UserPrefix(userId) + ":session:" + sessionId, UserPrefix(userId) + ":sessions", UserPrefix(userId) + ":state"];
    }

    private RedisKey[] TransitionKeys(long userId)
    {
        return [UserPrefix(userId) + ":sessions", UserPrefix(userId) + ":state"];
    }

    // 사용자별 hash tag를 유지하여 관련 key가 Redis Cluster에서도 같은 slot에 놓인다.
    private string UserPrefix(long userId)
    {
        return keyPrefix + ":user:{" + userId.ToString(CultureInfo.InvariantCulture) + "}";
    }

    private long NowMilliseconds => timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    private long StateTtlMilliseconds => (long)options.TransitionStateTtl.TotalMilliseconds;

    private const string RegisterScript = """
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', ARGV[5])
        redis.call('PSETEX', KEYS[1], ARGV[3], ARGV[1])
        redis.call('ZADD', KEYS[2], ARGV[4], ARGV[2])
        redis.call('PEXPIRE', KEYS[2], ARGV[3])
        if redis.call('EXISTS', KEYS[3]) == 1 then
            redis.call('PEXPIRE', KEYS[3], ARGV[6])
        end
        return redis.call('ZCARD', KEYS[2])
        """;

    private const string RefreshScript = """
        local owner = redis.call('GET', KEYS[1])
        if not owner or owner ~= ARGV[1] then
            return 0
        end
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', ARGV[5])
        redis.call('PSETEX', KEYS[1], ARGV[3], ARGV[1])
        redis.call('ZADD', KEYS[2], ARGV[4], ARGV[2])
        redis.call('PEXPIRE', KEYS[2], ARGV[3])
        if redis.call('EXISTS', KEYS[3]) == 1 then
            redis.call('PEXPIRE', KEYS[3], ARGV[6])
        end
        return 1
        """;

    private const string RemoveScript = """
        redis.call('DEL', KEYS[1])
        redis.call('ZREM', KEYS[2], ARGV[1])
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', ARGV[2])
        local count = redis.call('ZCARD', KEYS[2])
        if count == 0 then
            redis.call('DEL', KEYS[2])
        end
        if redis.call('EXISTS', KEYS[3]) == 1 then
            redis.call('PEXPIRE', KEYS[3], ARGV[3])
        end
        return count
        """;

    private const string CountScript = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
        local count = redis.call('ZCARD', KEYS[1])
        if count == 0 then
            redis.call('DEL', KEYS[1])
        end
        return count
        """;

    private const string ClaimOnlineScript = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
        if redis.call('ZCARD', KEYS[1]) == 0 then
            redis.call('DEL', KEYS[1])
            return 0
        end
        local current = redis.call('GET', KEYS[2])
        if current == ARGV[2] then
            redis.call('PEXPIRE', KEYS[2], ARGV[3])
            return 0
        end
        redis.call('PSETEX', KEYS[2], ARGV[3], ARGV[2])
        return 1
        """;

    private const string ClaimOfflineScript = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
        if redis.call('ZCARD', KEYS[1]) > 0 then
            return 0
        end
        redis.call('DEL', KEYS[1])
        local current = redis.call('GET', KEYS[2])
        if current == ARGV[2] then
            redis.call('PEXPIRE', KEYS[2], ARGV[3])
            return 0
        end
        redis.call('PSETEX', KEYS[2], ARGV[3], ARGV[2])
        return 1
        """;
}
