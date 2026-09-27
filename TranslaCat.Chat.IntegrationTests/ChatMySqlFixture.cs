using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MySql.Data.MySqlClient;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ChatMySqlCollection : ICollectionFixture<ChatMySqlFixture>
{
    public const string Name = "Owned CHAT MySQL";
}

public sealed class ChatMySqlFixture : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = null!;
    public string Catalog { get; private set; } = null!;
    public IDbContextFactory<ChatDbContext> Contexts { get; private set; } = null!;
    public static DateTime Epoch { get; } = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Unspecified);

    public async Task InitializeAsync()
    {
        // Docker label은 실행 wrapper가 검증한다. fixture도 실행 소유권·loopback·포트를 다시 제한한다.
        var manifestPath = Environment.GetEnvironmentVariable("CHAT_TEST_MANIFEST")
            ?? throw new InvalidOperationException("Owned integration manifest is required. No fake database fallback exists.");
        var runId = Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID");
        var root = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        Assert.True(root["testMode"]!.GetValue<bool>());
        Assert.Equal(root["runId"]!.GetValue<string>(), runId);
        Assert.Matches("^[a-f0-9]{32}$", runId!);

        var connection = new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CHAT_TEST_MYSQL")
            ?? throw new InvalidOperationException("CHAT_TEST_MYSQL is required."));
        Assert.Equal("127.0.0.1", connection.Server);
        Assert.Equal(root["mysqlPort"]!.GetValue<uint>(), connection.Port);
        Assert.True(string.IsNullOrEmpty(connection.Database));
        Catalog = "translacat_chat_test_" + runId;

        // 생성 전에 정확한 catalog를 manifest에 기록한다. 기존 catalog 삭제/초기화는 하지 않는다.
        var catalogs = root["catalogs"]!.AsArray();
        if (!catalogs.Any(node => node?.GetValue<string>() == Catalog))
        {
            catalogs.Add(Catalog);
            await File.WriteAllTextAsync(manifestPath, root.ToJsonString(new()
            {
                WriteIndented = true
            }));
        }

        await using (var server = new MySqlConnection(connection.ConnectionString))
        {
            await server.OpenAsync();
            Assert.StartsWith("8.4.", server.ServerVersion);
            await using var create = server.CreateCommand();
            create.CommandText = $"CREATE DATABASE IF NOT EXISTS `{Catalog}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci";
            await create.ExecuteNonQueryAsync();
        }

        connection.Database = Catalog;
        ConnectionString = ChatDatabaseTarget.Validate(connection.ConnectionString, allowIsolatedTestCatalog: true);
        Contexts = new PooledDbContextFactory<ChatDbContext>(new DbContextOptionsBuilder<ChatDbContext>()
            .UseMySQL(ConnectionString).Options);
        await using var context = await Contexts.CreateDbContextAsync();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        // 합성 데이터와 migration history는 결과 확인을 위해 남긴다. 별도 소유권 검사 cleanup이 정리한다.
        return Task.CompletedTask;
    }

    public async Task<SeededReadRoom> SeedReadRoomAsync(string roomType = "GROUP", long userId = 401)
    {
        await using var context = await Contexts.CreateDbContextAsync();
        var room = new ChatRoomEntity { RoomType = roomType, CreatedAt = Epoch, UpdatedAt = Epoch };
        context.ChatRooms.Add(room);
        await context.SaveChangesAsync();

        var member = new ChatRoomMemberEntity
        {
            ChatRoomId = room.Id,
            UserId = userId,
            Role = "MEMBER",
            JoinedAt = Epoch,
            CreatedAt = Epoch,
            UpdatedAt = Epoch
        };
        var first = Message(room.Id, userId + 1, Epoch.AddSeconds(1));
        var second = Message(room.Id, userId + 1, Epoch.AddSeconds(2));
        context.ChatRoomMembers.Add(member);
        context.ChatMessages.AddRange(first, second);
        await context.SaveChangesAsync();
        return new(room.Id, member.Id, userId, first.Id, second.Id);
    }

    public static ChatMessageEntity Message(long roomId, long? sender, DateTime at)
    {
        return new()
        {
            ChatRoomId = roomId,
            SenderUserId = sender,
            SenderType = "USER",
            MessageType = "TEXT",
            Content = "합성 메시지 日本語 😺",
            Status = "SENT",
            CreatedAt = at,
            UpdatedAt = at
        };
    }
}

public sealed record SeededReadRoom(long RoomId, long MemberId, long UserId, long FirstId, long SecondId);
