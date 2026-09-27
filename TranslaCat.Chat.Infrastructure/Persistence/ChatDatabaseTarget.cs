using MySql.Data.MySqlClient;

namespace TranslaCat.Chat.Infrastructure.Persistence;

public static class ChatDatabaseTarget
{
    public const string DefaultCatalog = "translacat_chat";

    public static string Validate(string connectionString, bool allowIsolatedTestCatalog = false)
    {
        MySqlConnectionStringBuilder connection;
        try
        {
            connection = new MySqlConnectionStringBuilder(connectionString);
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException or NotSupportedException)
        {
            // driver parser는 잘못된 옵션/값을 예외에 포함한다. runtime과 design-time 모두 원문을 숨긴다.
            throw new InvalidOperationException("CHAT DB 연결 설정 형식이 올바르지 않습니다.");
        }

        var catalog = connection.Database;
        var isolatedTest = allowIsolatedTestCatalog
            && System.Text.RegularExpressions.Regex.IsMatch(catalog, "^translacat_chat_test_[a-f0-9]{32}$");

        // 엔진 연결을 만들기 전에 catalog를 제한한다. BE/LL catalog로의 fallback은 없다.
        if (catalog != DefaultCatalog && !isolatedTest)
        {
            throw new InvalidOperationException("CHAT 전용 catalog만 사용할 수 있습니다.");
        }

        if (string.IsNullOrWhiteSpace(connection.Server))
        {
            throw new InvalidOperationException("CHAT DB host를 명시해야 합니다.");
        }

        return connection.ConnectionString;
    }
}
