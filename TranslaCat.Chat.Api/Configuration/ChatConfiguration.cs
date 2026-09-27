using System.Security.Cryptography;
using System.Text;
using TranslaCat.Chat.Api.Authentication;
using TranslaCat.Chat.Api.ServiceAuthentication;
using TranslaCat.Chat.Infrastructure.Configuration;

namespace TranslaCat.Chat.Api.Configuration;

public static class ChatConfiguration
{
    public static readonly IReadOnlyList<string> SecretKeys = Array.AsReadOnly(new[]
    {
        "Chat:Database:ConnectionString",
        "Chat:Redis:ConnectionString",
        "Chat:Redis:Password",
        "Chat:Authentication:Base64SigningKey",
        "Chat:ServiceAuthentication:Ingress:Base64SigningKey",
        "Chat:Identity:ServiceAuthentication:Base64SigningKey",
        "Chat:Core:ServiceAuthentication:Base64SigningKey",
        "Chat:Ai:ApiKey",
        "Chat:Translation:ApiKey"
    });

    public static void ValidateEnvironment(string? dotnetEnvironment, string? aspnetEnvironment)
    {
        // 환경명은 host 생성 전에 확정한다. Local/Prod 별칭과 대소문자 차이를 묵인하지 않는다.
        if (!string.IsNullOrEmpty(dotnetEnvironment) && !string.IsNullOrEmpty(aspnetEnvironment)
            && !string.Equals(dotnetEnvironment, aspnetEnvironment, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("DOTNET_ENVIRONMENT and ASPNETCORE_ENVIRONMENT must match.");
        }

        var name = dotnetEnvironment ?? aspnetEnvironment ?? Environments.Production;
        if (name != Environments.Development && name != Environments.Production)
        {
            throw new InvalidOperationException("Chat environment must be Development or Production.");
        }
    }

    public static void AddSecretFiles(ConfigurationManager configuration)
    {
        // 등록되지 않은 FILE 이름은 자동 지원되지 않는다. 오타로 인증값이 누락되는 것을 시작 전에 막는다.
        var supportedFiles = SecretKeys.Select(key => $"{key}_FILE").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (configuration.GetSection("Chat").AsEnumerable().Any(setting =>
            setting.Key.EndsWith("_FILE", StringComparison.OrdinalIgnoreCase)
            && !supportedFiles.Contains(setting.Key)))
        {
            throw new InvalidOperationException("Unsupported Chat secret-file setting.");
        }

        // 기본 JSON/User Secrets/env/CLI provider 뒤에서 명시적 파일만 읽는다. 값+파일 충돌은 거절한다.
        var values = new Dictionary<string, string?>();
        foreach (var key in SecretKeys)
        {
            var path = configuration[$"{key}_FILE"];
            if (!string.IsNullOrWhiteSpace(path))
            {
                values[key] = ChatSecretFile.Resolve(configuration[key], path, key);
            }
        }
        configuration.AddInMemoryCollection(values);
    }

    public static void Validate(ConfigurationManager configuration, string environmentName)
    {
        ValidateEnvironment(environmentName, null);
        ChatOutboundConfiguration.Validate(configuration, environmentName);

        var jwt = configuration.GetSection(ChatJwtOptions.SectionName).Get<ChatJwtOptions>() ?? new();
        if (jwt.Enabled && !HasUserSigningKey(jwt.Base64SigningKey))
        {
            throw new InvalidOperationException("Chat user JWT verification key is incomplete.");
        }

        // 명시적으로 활성화한 인증 기능은 빠진 필수값을 시작 시점에 알려준다.
        var ingress = configuration.GetSection(ChatServiceIngressOptions.SectionName).Get<ChatServiceIngressOptions>() ?? new();
        if (ingress.Enabled && !ChatServiceJwt.IsConfigured(ingress))
        {
            throw new InvalidOperationException("Chat service ingress authentication is incomplete.");
        }
        var identity = configuration.GetSection(ChatIdentityOptions.SectionName).Get<ChatIdentityOptions>() ?? new();
        if (identity.Enabled && (!ChatServiceJwt.IsConfigured(identity.ServiceAuthentication)
            || identity.TimeoutSeconds is < 1 or > 30
            || !Uri.TryCreate(identity.BaseUrl, UriKind.Absolute, out var identityUri)
            || identityUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(identityUri.UserInfo)
            || identityUri.AbsolutePath != "/" || !string.IsNullOrEmpty(identityUri.Query) || !string.IsNullOrEmpty(identityUri.Fragment)
            || (identityUri.Scheme != "https" && !(environmentName == Environments.Development && identityUri.IsLoopback))))
        {
            throw new InvalidOperationException("Chat identity authentication or origin is incomplete.");
        }

        // 서로 다른 호출 방향과 사용자 서명 키는 같은 비밀을 사용하지 않는다.
        // reply/translation은 동일한 CHAT→AI 권한의 두 기능이므로 서로 같은 값은 허용한다.
        var credentials = new[]
        {
            new[] { "Chat:Authentication:Base64SigningKey" },
            new[] { "Chat:ServiceAuthentication:Ingress:Base64SigningKey" },
            new[] { "Chat:Identity:ServiceAuthentication:Base64SigningKey", "Chat:Core:ServiceAuthentication:Base64SigningKey" },
            new[] { "Chat:Ai:ApiKey", "Chat:Translation:ApiKey" }
        };
        for (var left = 0; left < credentials.Length; left++)
        {
            for (var right = left + 1; right < credentials.Length; right++)
            {
                foreach (var leftKey in credentials[left])
                {
                    foreach (var rightKey in credentials[right])
                    {
                        var first = configuration[leftKey];
                        var second = configuration[rightKey];
                        if (!string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
                            && EqualSecret(NormalizeSigningKey(leftKey, first), NormalizeSigningKey(rightKey, second)))
                        {
                            throw new InvalidOperationException($"Separate credentials are required for {leftKey} and {rightKey}.");
                        }
                    }
                }
            }
        }

        if (environmentName == Environments.Production && configuration.GetSection("Chat:Testing").Exists())
        {
            throw new InvalidOperationException("Test configuration is not allowed in Production.");
        }
    }

    private static bool EqualSecret(string first, string second)
    {
        return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(first.Trim())),
                SHA256.HashData(Encoding.UTF8.GetBytes(second.Trim())));
    }

    private static string NormalizeSigningKey(string key, string value)
    {
        if (!key.EndsWith(":Base64SigningKey", StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            // Base64의 무시되는 공백으로 같은 key bytes의 방향별 재사용 검사를 피하지 못한다.
            return Convert.ToBase64String(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return value;
        }
    }

    private static bool HasUserSigningKey(string? value)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(value) && Convert.FromBase64String(value).Length >= 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
