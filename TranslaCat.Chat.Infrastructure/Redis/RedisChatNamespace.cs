namespace TranslaCat.Chat.Infrastructure.Redis;

public static class RedisChatNamespace
{
    public static void Validate(string prefix)
    {
        // 배포 환경/test run 범위는 key뿐 아니라 DB 번호와 무관한 channel에도 사용한다.
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (!prefix.StartsWith("translacat:chat:", StringComparison.Ordinal)
            || prefix.Length <= "translacat:chat:".Length
            || prefix.Any(character => char.IsWhiteSpace(character) || character is '{' or '}' or '*' or '?'))
        {
            throw new ArgumentException("Redis namespace must use translacat:chat:<environment-or-run> without wildcard/hash tags.");
        }
    }
}
