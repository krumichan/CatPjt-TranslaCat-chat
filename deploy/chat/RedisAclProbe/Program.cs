using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using StackExchange.Redis;

try
{
    // 일반 endpoint/secret을 인자로 받지 않는다. 소유권 검사를 마친 합성 manifest만 읽는다.
    if (args.Length != 1)
    {
        throw new InvalidOperationException("An owned ACL validation manifest is required.");
    }
    using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(args[0]));
    var root = manifest.RootElement;
    Require(root.GetProperty("testMode").GetBoolean(), "test-mode");
    Require(root.GetProperty("owner").GetString() == "chat-redis-acl-validation", "owner");
    var runId = root.GetProperty("runId").GetString()!;
    Require(Regex.IsMatch(runId, "^[a-f0-9]{32}$"), "run-id");
    var prefix = root.GetProperty("keyPrefix").GetString()!;
    Require(prefix == "translacat:chat:test:" + runId, "namespace");
    var password = (await File.ReadAllTextAsync(root.GetProperty("passwordFile").GetString()!)).Trim();
    Require(Regex.IsMatch(password, "^[a-f0-9]{64}$"), "secret-format");
    var port = root.GetProperty("port").GetInt32();
    var configuration = $"127.0.0.1:{port},user=chat,password={password},abortConnect=true,connectTimeout=3000,syncTimeout=3000,configChannel={prefix}:configuration,tiebreaker={prefix}:tiebreaker";
    var options = ConfigurationOptions.Parse(configuration);
    options.AllowAdmin = true; // 실제 server ACL의 위험 명령 거부를 확인하기 위한 test client다.
    using var first = await ConnectionMultiplexer.ConnectAsync(options);
    using var second = await ConnectionMultiplexer.ConnectAsync(options);
    var database = first.GetDatabase();
    var checks = 0;

    // 실제 client handshake와 lease용 Lua/명령이 제한된 ACL 안에서 동작하는지 확인한다.
    await database.PingAsync();
    checks++;
    var keys = new RedisKey[] { prefix + ":user:{73}:session:probe", prefix + ":user:{73}:sessions", prefix + ":user:{73}:state" };
    const string register = """
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', ARGV[5])
        redis.call('PSETEX', KEYS[1], ARGV[3], ARGV[1])
        redis.call('ZADD', KEYS[2], ARGV[4], ARGV[2])
        redis.call('PEXPIRE', KEYS[2], ARGV[3])
        if redis.call('EXISTS', KEYS[3]) == 1 then
            redis.call('PEXPIRE', KEYS[3], ARGV[6])
        end
        return redis.call('ZCARD', KEYS[2])
        """;
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var registered = await database.ScriptEvaluateAsync(register, keys,
        [73, "probe", 60000, now + 60000, now, 110000]);
    Require((long)registered == 1, "lua-register");
    checks++;
    Require(await database.KeyTimeToLiveAsync(keys[0]) > TimeSpan.Zero, "native-ttl");
    checks++;
    Require((string?)await database.StringGetAsync(keys[0]) == "73", "lease-owner");
    checks++;

    // 별도 multiplexer 사이의 실제 채널 권한과 전달을 확인한다.
    var channel = RedisChannel.Literal(prefix + ":presence:events");
    var queue = await second.GetSubscriber().SubscribeAsync(channel);
    var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    queue.OnMessage(message => received.TrySetResult(message.Message.ToString()));
    await first.GetSubscriber().PublishAsync(channel, "synthetic-presence");
    Require(await received.Task.WaitAsync(TimeSpan.FromSeconds(5)) == "synthetic-presence", "cross-client-pubsub");
    await queue.UnsubscribeAsync();
    checks++;

    // 허용 namespace 밖 key/channel 및 관리 명령은 Redis가 NOPERM으로 거부해야 한다.
    await DeniedAsync(() => database.StringGetAsync("other-service:secret"), "foreign-key");
    checks++;
    await DeniedAsync(() => first.GetSubscriber().PublishAsync(RedisChannel.Literal("other-service:events"), "synthetic"), "foreign-publish");
    checks++;
    await DeniedAsync(() => second.GetSubscriber().SubscribeAsync(RedisChannel.Literal("other-service:events")), "foreign-subscribe");
    checks++;
    await DeniedAsync(() => database.ScriptEvaluateAsync("return redis.call('GET', KEYS[1])", ["other-service:secret"]), "foreign-lua-key");
    checks++;
    await DeniedAsync(() => database.ExecuteAsync("FLUSHDB"), "flushdb");
    checks++;
    await DeniedAsync(() => database.ExecuteAsync("CONFIG", "SET", "maxmemory", "1"), "config-set");
    checks++;

    // 잘못된 자격증명을 동일 ACL 사용자에게 보내도 연결 성공으로 처리하지 않는다.
    var invalid = ConfigurationOptions.Parse(configuration);
    invalid.Password = new string('0', 64);
    var rejected = false;
    try
    {
        using var unexpected = await ConnectionMultiplexer.ConnectAsync(invalid);
    }
    catch (RedisConnectionException)
    {
        rejected = true;
    }
    Require(rejected, "invalid-credentials");
    checks++;

    Console.WriteLine($"PASS: {checks} actual StackExchange.Redis ACL checks.");
    return 0;
}
catch (Exception exception)
{
    // 비밀번호/연결 문자열/실제 payload를 오류 출력에 포함하지 않는다.
    Console.Error.WriteLine("FAIL: Redis ACL probe (" + exception.GetType().Name + ").");
    return 1;
}

static void Require(bool condition, string operation)
{
    if (!condition)
    {
        throw new InvalidOperationException("Redis ACL assertion failed: " + operation);
    }
}

static async Task DeniedAsync(Func<Task> action, string operation)
{
    try
    {
        await action();
    }
    catch (RedisServerException exception) when (exception.Message.StartsWith("NOPERM", StringComparison.Ordinal))
    {
        return;
    }
    throw new InvalidOperationException("Redis ACL did not deny: " + operation);
}
