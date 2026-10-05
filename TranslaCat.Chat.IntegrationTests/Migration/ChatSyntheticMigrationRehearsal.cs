using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.IntegrationTests.Migration;

// 테스트 어셈블리 전용 합성 리허설이다. 임의 source/target 연결 문자열을 받는 운영 이관 도구가 아니다.
internal sealed class ChatSyntheticMigrationRehearsal
{
    private readonly string manifestPath;
    private readonly string rehearsalId;
    private readonly string sourceConnection;
    private readonly string targetConnection;
    public string SourceCatalog
    {
        get;
    }
    public string TargetCatalog
    {
        get;
    }

    private ChatSyntheticMigrationRehearsal(string manifestPath, string rehearsalId,
        string sourceCatalog, string targetCatalog, string sourceConnection, string targetConnection)
    {
        this.manifestPath = manifestPath;
        this.rehearsalId = rehearsalId;
        SourceCatalog = sourceCatalog;
        TargetCatalog = targetCatalog;
        this.sourceConnection = sourceConnection;
        this.targetConnection = targetConnection;
    }

    public static async Task<ChatSyntheticMigrationRehearsal> CreateOwnedAsync()
    {
        // wrapper 검증에 더해 testMode, loopback, 정확한 container ID/label을 생성 직전에 다시 확인한다.
        string manifest = Environment.GetEnvironmentVariable("CHAT_TEST_MANIFEST")
            ?? throw new InvalidOperationException("An owned test manifest is required.");
        string rootPath = FindWorkspace();
        string path = Path.GetFullPath(manifest);
        // 이동된 검증 manifest도 CHAT 전용 중앙 runtime 경계 안에서만 허용한다.
        string runtimeRoot = Path.GetFullPath(Path.Combine(rootPath, "..", ".codex-workspace",
            "verification", "chat", "TestResults", "V2", "Runtime"));
        if (!path.StartsWith(runtimeRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The manifest is outside the owned test runtime directory.");
        }

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        string runId = root["runId"]!.GetValue<string>();
        string containerId = root["mysql"]!.GetValue<string>();
        if (root["testMode"]?.GetValue<bool>() != true || !Regex.IsMatch(runId, "^[a-f0-9]{32}$")
            || !Regex.IsMatch(containerId, "^[a-f0-9]{64}$")
            || runId != Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID"))
        {
            throw new InvalidOperationException("Invalid owned test runtime identity.");
        }
        await VerifyContainerAsync(containerId, runId);
        var server = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CHAT_TEST_MYSQL")
            ?? throw new InvalidOperationException("Owned MySQL connection is required."));
        if (server.Server != "127.0.0.1" || server.Port != root["mysqlPort"]!.GetValue<uint>()
            || !string.IsNullOrEmpty(server.Database))
        {
            throw new InvalidOperationException("Only the manifest loopback server without a default catalog is allowed.");
        }

        // 기존/default/공유 catalog를 입력받지 않는다. 새 이름은 각자 32자리 GUID이고 manifest에 먼저 등록한다.
        string source = "translacat_chat_test_" + Guid.NewGuid().ToString("N");
        string target = "translacat_chat_test_" + Guid.NewGuid().ToString("N");
        ValidateFreshCatalogPair(source, target, root["catalogs"]!.AsArray().Select(item => item!.GetValue<string>()));
        string id = Guid.NewGuid().ToString("N");
        root["catalogs"]!.AsArray().Add(source);
        root["catalogs"]!.AsArray().Add(target);
        var rehearsals = root["syntheticMigrationRehearsals"] as JsonArray ?? [];
        if (root["syntheticMigrationRehearsals"] is null)
        {
            root["syntheticMigrationRehearsals"] = rehearsals;
        }

        rehearsals.Add(new JsonObject
        {
            ["id"] = id,
            ["parentRunId"] = runId,
            ["sourceCatalog"] = source,
            ["targetCatalog"] = target,
            ["purpose"] = "synthetic-only-14-table-copy",
            ["createdUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["status"] = "CREATING"
        });
        await File.WriteAllTextAsync(path, root.ToJsonString(new()
        {
            WriteIndented = true
        }));

        await using (var connection = new MySqlConnection(server.ConnectionString))
        {
            await connection.OpenAsync();
            if (!connection.ServerVersion.StartsWith("8.4.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("This rehearsal only validates the owned MySQL 8.4 runtime.");
            }

            foreach (string catalog in new[] { source, target })
            {
                await using var exists = connection.CreateCommand();
                exists.CommandText = "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @catalog";
                exists.Parameters.AddWithValue("@catalog", catalog);
                if (Convert.ToInt64(await exists.ExecuteScalarAsync()) != 0)
                {
                    throw new InvalidOperationException("The proposed rehearsal catalog already exists.");
                }

                await using var create = connection.CreateCommand();
                create.CommandText = $"CREATE DATABASE `{catalog}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci";
                await create.ExecuteNonQueryAsync();
            }
        }

        server.Database = source;
        string sourceConnection = ChatDatabaseTarget.Validate(server.ConnectionString, allowIsolatedTestCatalog: true);
        server.Database = target;
        string targetConnection = ChatDatabaseTarget.Validate(server.ConnectionString, allowIsolatedTestCatalog: true);
        var result = new ChatSyntheticMigrationRehearsal(path, id, source, target, sourceConnection, targetConnection);
        await using var sourceContext = result.Source();
        await using var targetContext = result.Target();
        await sourceContext.Database.MigrateAsync();
        await targetContext.Database.MigrateAsync();
        await result.RecordAsync("SCHEMA_READY", null);
        return result;
    }

    public static void ValidateFreshCatalogPair(string source, string target, IEnumerable<string> existing)
    {
        var occupied = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!Regex.IsMatch(source, "^translacat_chat_test_[a-f0-9]{32}$")
            || !Regex.IsMatch(target, "^translacat_chat_test_[a-f0-9]{32}$")
            || source == target || occupied.Contains(source) || occupied.Contains(target))
        {
            throw new InvalidOperationException("Only two new distinct isolated test catalogs are allowed.");
        }
    }

    public ChatDbContext Source()
    {
        return Context(sourceConnection);
    }

    public ChatDbContext Target()
    {
        return Context(targetConnection);
    }

    public async Task CopyAsync(bool failAfterRoomMembers = false)
    {
        await using var source = Source();
        await using var target = Target();
        await using var sourceTransaction = await source.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await using var targetTransaction = await target.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);

        // 대상이 조금이라도 채워져 있으면 재실행을 거절한다. merge/upsert나 기존 행 삭제는 하지 않는다.
        if ((await DigestsAsync(target)).Any(table => table.Count != 0))
        {
            throw new InvalidOperationException("The rehearsal target is not empty; rerun is refused.");
        }

        // 독립 context 사이에서 행 값만 이동한다. cross-database SQL/join/FK는 사용하지 않는다.
        await CopyTableAsync<ChatRoomEntity>(source, target);
        await CopyTableAsync<ChatAiAgentEntity>(source, target);
        await CopyTableAsync<ChatAiSystemSettingEntity>(source, target);
        await CopyTableAsync<UserChatLanguageSettingEntity>(source, target);
        await CopyTableAsync<ChatRoomMemberEntity>(source, target);
        if (failAfterRoomMembers)
        {
            throw new InvalidOperationException("Synthetic failure before dependent tables.");
        }

        await CopyTableAsync<OpenChatRoomEntity>(source, target);
        await CopyTableAsync<OpenChatMemberProfileEntity>(source, target);
        await CopyTableAsync<ChatRoomAiMemberEntity>(source, target);
        await CopyTableAsync<ChatRoomAiSettingEntity>(source, target);
        await CopyTableAsync<ChatMessageEntity>(source, target);
        await CopyTableAsync<ChatMessageTranslationEntity>(source, target);
        await CopyTableAsync<OpenChatBanEntity>(source, target);
        await CopyTableAsync<ChatRoomAiActivityEntity>(source, target);
        await CopyTableAsync<ChatNotificationEntity>(source, target);
        await targetTransaction.CommitAsync();
        await sourceTransaction.CommitAsync();
    }

    public static async Task<IReadOnlyList<TableDigest>> DigestsAsync(ChatDbContext context)
    {
        // 모든 mapped scalar를 비교하되 원문 데이터는 결과 파일/로그에 출력하지 않는다.
        return [await DigestAsync<ChatRoomEntity>(context), await DigestAsync<ChatRoomMemberEntity>(context),
            await DigestAsync<ChatMessageEntity>(context), await DigestAsync<ChatMessageTranslationEntity>(context),
            await DigestAsync<UserChatLanguageSettingEntity>(context), await DigestAsync<OpenChatRoomEntity>(context),
            await DigestAsync<OpenChatMemberProfileEntity>(context), await DigestAsync<OpenChatBanEntity>(context),
            await DigestAsync<ChatAiAgentEntity>(context), await DigestAsync<ChatRoomAiMemberEntity>(context),
            await DigestAsync<ChatRoomAiSettingEntity>(context), await DigestAsync<ChatRoomAiActivityEntity>(context),
            await DigestAsync<ChatAiSystemSettingEntity>(context), await DigestAsync<ChatNotificationEntity>(context)];
    }

    public async Task RecordAsync(string status, IReadOnlyList<TableDigest>? digests)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        var record = root["syntheticMigrationRehearsals"]!.AsArray()
            .Single(value => value!["id"]!.GetValue<string>() == rehearsalId)!.AsObject();
        record["status"] = status;
        record["updatedUtc"] = DateTimeOffset.UtcNow.ToString("O");
        if (digests is not null)
        {
            record["verifiedTables"] = JsonSerializer.SerializeToNode(digests);
        }

        await File.WriteAllTextAsync(manifestPath, root.ToJsonString(new()
        {
            WriteIndented = true
        }));
    }

    private static async Task CopyTableAsync<T>(ChatDbContext source, ChatDbContext target) where T : class
    {
        target.Set<T>().AddRange(await source.Set<T>().AsNoTracking().ToListAsync());
        await target.SaveChangesAsync();
    }

    private static async Task<TableDigest> DigestAsync<T>(ChatDbContext context) where T : class
    {
        var rows = await context.Set<T>().AsNoTracking().ToListAsync();
        string canonical = string.Join('\n', rows.Select(row => JsonSerializer.Serialize(row)).Order(StringComparer.Ordinal));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(context.Model.FindEntityType(typeof(T))!.GetTableName()!, rows.Count, hash);
    }

    private static ChatDbContext Context(string connection)
    {
        return new(new DbContextOptionsBuilder<ChatDbContext>().UseMySQL(connection).Options);
    }

    private static string FindWorkspace()
    {
        // 검증 산출물이 중앙 폴더에 있을 때도 원본 source와 실제 solution을 명시적으로 연결한다.
        var configured = Environment.GetEnvironmentVariable("CHAT_TEST_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured)
                || !File.Exists(Path.Combine(configured, "CatPjt-TranslaCat-chat.slnx")))
            {
                throw new InvalidOperationException("CHAT_TEST_WORKSPACE must be the absolute CHAT source root.");
            }
            return Path.GetFullPath(configured);
        }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CatPjt-TranslaCat-chat.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("CHAT workspace was not found.");
    }

    private static async Task VerifyContainerAsync(string id, string runId)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "inspect", "--format", "{{.Id}} {{index .Config.Labels \"translacat.chat.run\"}}", id })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Container verification could not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0 || output.Trim() != id + " " + runId)
        {
            throw new InvalidOperationException("The exact synthetic MySQL container ownership could not be verified.");
        }
    }
}

internal sealed record TableDigest(string Table, int Count, string Sha256);
