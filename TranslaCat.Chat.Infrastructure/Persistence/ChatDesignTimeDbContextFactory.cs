using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TranslaCat.Chat.Infrastructure.Configuration;

namespace TranslaCat.Chat.Infrastructure.Persistence;

public sealed class ChatDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args)
    {
        // design-time은 API/worker를 시작하지 않는다. DDL 연결은 명시적인 환경 설정만 사용한다.
        var connection = ChatSecretFile.Resolve(
            Environment.GetEnvironmentVariable("CHAT_DATABASE_CONNECTION"),
            Environment.GetEnvironmentVariable("CHAT_DATABASE_CONNECTION_FILE"),
            "CHAT_DATABASE_CONNECTION")
            ?? throw new InvalidOperationException("CHAT_DATABASE_CONNECTION을 명시해야 합니다.");
        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .UseMySQL(ChatDatabaseTarget.Validate(connection))
            .Options;

        return new ChatDbContext(options);
    }
}
