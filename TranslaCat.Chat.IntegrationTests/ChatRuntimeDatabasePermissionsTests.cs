using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Runtime;
using TranslaCat.Chat.Infrastructure.Persistence;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class ChatRuntimeDatabasePermissionsTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task Unmigrated_owned_catalog_remains_empty_after_runtime_startup_and_readiness()
    {
        // 준비: 기존 catalog를 비우지 않고 새 합성 catalog를 소유 manifest에 먼저 기록한다.
        var catalog = "translacat_chat_test_" + Guid.NewGuid().ToString("N");
        var manifestPath = Environment.GetEnvironmentVariable("CHAT_TEST_MANIFEST")!;
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        Assert.Equal(Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID"), manifest["runId"]!.GetValue<string>());
        Assert.True(manifest["testMode"]!.GetValue<bool>());
        manifest["catalogs"]!.AsArray().Add(catalog);
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(new()
        {
            WriteIndented = true
        }));
        var user = "chat_empty_" + Guid.NewGuid().ToString("N")[..20];
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(36));
        await using var admin = new MySqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAdminAsync(admin, $"CREATE DATABASE `{catalog}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
        await ExecuteAdminAsync(admin, $"CREATE USER '{user}'@'%' IDENTIFIED BY @password", password);
        await ExecuteAdminAsync(admin, $"GRANT SELECT, INSERT, UPDATE, DELETE ON `{catalog}`.* TO '{user}'@'%'");
        var connection = new MySqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = catalog,
            UserID = user,
            Password = password
        };

        // 실행: migration 권한이 없는 실제 runtime을 빈 DB에 연결한다. 실패 시 가짜 schema를 만들지 않는다.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddChatReadHttp();
        builder.Services.AddChatRuntime(builder.Configuration);
        builder.Services.AddChatDatabase(connection.ConnectionString, isolatedTestCatalog: true);
        await using var app = builder.Build();
        app.UseChatReadHttp();
        app.MapChatReadiness();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var response = await client.GetAsync("/api/ready");

        // 검증: 사용자 데이터뿐 아니라 migration history/seed table도 startup에서 생성되지 않는다.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var query = admin.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=@catalog";
        query.Parameters.AddWithValue("@catalog", catalog);
        Assert.Equal(0L, Convert.ToInt64(await query.ExecuteScalarAsync()));
        await app.StopAsync();
    }

    [Fact]
    public async Task Runtime_DML_user_starts_without_migration_and_cannot_modify_schema_or_grant_privileges()
    {
        // 준비: fixture가 소유권을 검증한 실행별 catalog에만 신규 사용자 권한을 부여한다.
        var user = "chat_dml_" + Guid.NewGuid().ToString("N")[..20];
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(36));
        await using var admin = new MySqlConnection(fixture.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAdminAsync(admin, $"CREATE USER '{user}'@'%' IDENTIFIED BY @password", password);
        await ExecuteAdminAsync(admin, $"GRANT SELECT, INSERT, UPDATE, DELETE ON `{fixture.Catalog}`.* TO '{user}'@'%'");
        var connection = new MySqlConnectionStringBuilder(fixture.ConnectionString) { UserID = user, Password = password };
        var seeded = await fixture.SeedReadRoomAsync();
        await using var before = await fixture.Contexts.CreateDbContextAsync();
        var migrationHistory = (await before.Database.GetAppliedMigrationsAsync()).ToArray();
        var roomCount = await before.ChatRooms.CountAsync();

        // 실행: Production 이름의 격리 Kestrel에 실제 runtime DI와 제한된 DB 계정을 연결한다.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddChatReadHttp();
        builder.Services.AddChatRuntime(builder.Configuration);
        builder.Services.AddChatDatabase(connection.ConnectionString, isolatedTestCatalog: true);
        await using var app = builder.Build();
        app.UseChatReadHttp();
        app.MapChatReadiness();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var health = await client.GetAsync("/api/health");
        using var ready = await client.GetAsync("/api/ready");

        // 검증: startup은 schema/seed를 쓰지 않는다. 외부 미구성 상태는 readiness 실패로 남긴다.
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        await using var check = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(migrationHistory, (await check.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(roomCount, await check.ChatRooms.CountAsync());
        Assert.NotNull(await check.ChatRooms.FindAsync(seeded.RoomId));

        await using var runtime = new MySqlConnection(connection.ConnectionString);
        await runtime.OpenAsync();
        Assert.Equal(1142, await DeniedAsync(runtime, "CREATE TABLE runtime_should_not_create (id BIGINT)"));
        Assert.Equal(1142, await DeniedAsync(runtime, "ALTER TABLE chat_room ADD COLUMN runtime_should_not_alter INT"));

        // MySQL은 테이블 DDL은 1142, GRANT OPTION 부재는 DB 권한 오류 1044로 구분한다.
        Assert.Equal(1044, await DeniedAsync(runtime, $"GRANT SELECT ON `{fixture.Catalog}`.* TO '{user}'@'%'"));

        // 실행 / 검증: 같은 제한 계정으로 업무 EF 읽기·갱신은 실제 성공한다.
        await using var scoped = app.Services.CreateAsyncScope();
        var contexts = scoped.ServiceProvider.GetRequiredService<IDbContextFactory<ChatDbContext>>();
        await using var data = await contexts.CreateDbContextAsync();
        var room = await data.ChatRooms.SingleAsync(value => value.Id == seeded.RoomId);
        room.Name = "합성 DML 권한 검증";
        await data.SaveChangesAsync();
        Assert.Equal("합성 DML 권한 검증", (await check.ChatRooms.AsNoTracking().SingleAsync(value => value.Id == seeded.RoomId)).Name);
        await app.StopAsync();
    }

    private static async Task ExecuteAdminAsync(MySqlConnection connection, string sql, string? password = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (password is not null)
        {
            command.Parameters.AddWithValue("@password", password);
        }

        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (MySqlException error)
        {
            // 자격증명 provisioning 실패에도 SQL·password 원문을 테스트 출력에 포함하지 않는다.
            throw new InvalidOperationException($"Owned runtime DB provisioning failed (MySQL {error.Number}).");
        }
    }

    private static async Task<int> DeniedAsync(MySqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        try
        {
            await command.ExecuteNonQueryAsync();
            return 0;
        }
        catch (MySqlException error)
        {
            return error.Number;
        }
    }
}
