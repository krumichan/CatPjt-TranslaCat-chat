using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Infrastructure.Configuration;
using TranslaCat.Chat.Infrastructure.Persistence;

namespace TranslaCat.Chat.Api.Runtime;

// 배포 전용 진입점. API/AI/Redis 작업자를 시작하지 않고 명시적인 DDL 자격만 받는다.
public static class ChatMigrationCommand
{
    public static async Task<int> RunAsync()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("CHAT_ALLOW_MIGRATIONS") != "true")
                throw new InvalidOperationException("Migration approval is required.");
            var connection = ChatSecretFile.Resolve(null,
                Environment.GetEnvironmentVariable("CHAT_MIGRATION_CONNECTION_FILE"),
                "CHAT_MIGRATION_CONNECTION")
                ?? throw new InvalidOperationException("Migration connection is missing.");
            var options = new DbContextOptionsBuilder<ChatDbContext>()
                .UseMySQL(ChatDatabaseTarget.Validate(connection)).Options;
            await using var database = new ChatDbContext(options);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            await database.Database.MigrateAsync(deadline.Token);
            if ((await database.Database.GetPendingMigrationsAsync(deadline.Token)).Any())
                throw new InvalidOperationException("Pending migrations remain.");
            Console.WriteLine("CHAT_MIGRATION_COMPLETE");
            return 0;
        }
        catch (Exception failure)
        {
            // 드라이버 예외에는 접속 문자열/SQL이 포함될 수 있다. 원문은 CI에 출력하지 않는다.
            Console.Error.WriteLine($"CHAT_MIGRATION_FAILED: {failure.GetType().Name}");
            return 1;
        }
    }
}
