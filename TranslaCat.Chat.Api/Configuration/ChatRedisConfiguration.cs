using StackExchange.Redis;
using TranslaCat.Chat.Infrastructure.Redis;

namespace TranslaCat.Chat.Api.Configuration;

public static class ChatRedisConfiguration
{
    public static string? GetConnectionString(IConfiguration configuration)
    {
        var section = configuration.GetSection("Chat:Redis");
        var legacy = section["ConnectionString"];
        var endpoint = section["Endpoint"];
        var password = section["Password"];
        var user = section["User"];
        var structured = new[] { endpoint, password, user }.Any(value => !string.IsNullOrWhiteSpace(value));

        // 기존 연결 문자열과 새 secret-file 연결 방식은 동시에 사용하지 않는다.
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            if (structured)
            {
                throw new InvalidOperationException("Choose one Chat Redis connection configuration.");
            }

            return legacy;
        }
        if (!structured)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(password)
            || string.IsNullOrWhiteSpace(user) || endpoint.Contains(',') || endpoint.Contains('=')
            || password.Any(char.IsControl) || user.Any(char.IsControl)
            || password.Contains(',') || user.Contains(',')
            || password != password.Trim() || user != user.Trim())
        {
            // client가 문자열을 다시 파싱할 때 credential을 자르거나 다른 옵션으로 해석할 값은 거절한다.
            throw new InvalidOperationException("Chat Redis endpoint, ACL user and password are required.");
        }

        try
        {
            var options = ConfigurationOptions.Parse(endpoint);
            options.User = user;
            options.Password = password;
            options.Ssl = section.GetValue<bool>("UseTls");
            options.AbortOnConnectFail = false;
            var prefix = section["Namespace"] ?? throw new InvalidOperationException();
            RedisChatNamespace.Validate(prefix);
            options.ConfigurationChannel = $"{prefix}:configuration";
            options.TieBreaker = $"{prefix}:tiebreaker";
            if (options.EndPoints.Count != 1 || (!options.Ssl && !section.GetValue<bool>("AllowPrivatePlaintext")))
            {
                throw new InvalidOperationException();
            }
            return options.ToString(includePassword: true);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException("Chat Redis requires one endpoint and TLS or explicit private-network plaintext.");
        }
    }
}
