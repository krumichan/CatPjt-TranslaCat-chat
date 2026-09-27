using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.Translation;

namespace TranslaCat.Chat.Api.Configuration;

public static class ChatOutboundConfiguration
{
    public static void Validate(IConfiguration configuration, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        // 실제 소비하는 Options를 그대로 읽는다. 키의 인코딩이나 업무 retry/worker 정책을 바꾸지 않는다.
        ValidateSection("Chat:Ai", () =>
        {
            var options = configuration.GetSection("Chat:Ai").Get<ChatAiOptions>() ?? new();
            options.Validate();
            ValidateEnabled("Chat:Ai", options.Enabled, options.AiBaseUri, options.ApiKey, environmentName);
        });
        ValidateSection("Chat:Translation", () =>
        {
            var options = configuration.GetSection("Chat:Translation").Get<ChatTranslationOptions>() ?? new();
            options.Validate();
            ValidateEnabled("Chat:Translation", options.Enabled, options.AiBaseUri, options.ApiKey, environmentName);
        });
    }

    private static void ValidateEnabled(string section, bool enabled, Uri? endpoint, string? apiKey, string environmentName)
    {
        // 비활성 기본값을 운영 연결로 승격하지 않는다. 활성화를 요청한 구성은 누락 상태로 기동하지 않는다.
        if (!enabled)
        {
            return;
        }

        if (endpoint is null || !endpoint.IsAbsoluteUri || string.IsNullOrWhiteSpace(apiKey))
        {
            throw InvalidConfiguration(section);
        }

        bool development = environmentName.Equals("Development", StringComparison.OrdinalIgnoreCase);
        bool production = environmentName.Equals("Production", StringComparison.OrdinalIgnoreCase);
        bool secureEndpoint = endpoint.Scheme == Uri.UriSchemeHttps;
        bool localDevelopmentEndpoint = development && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback;
        if (!secureEndpoint && !localDevelopmentEndpoint)
        {
            throw InvalidConfiguration(section);
        }

        // 명백한 예제 문자열을 실제 운영 자격증명으로 오인하지 않는다. entropy나 서버 scope를 검증한 것은 아니다.
        if (production && IsPlaceholder(apiKey))
        {
            throw InvalidConfiguration(section);
        }
    }

    private static bool IsPlaceholder(string value)
    {
        string normalized = value.Trim();
        if ((normalized.StartsWith('<') && normalized.EndsWith('>'))
            || normalized.Contains("${", StringComparison.Ordinal)
            || (normalized.StartsWith('%') && normalized.EndsWith('%')))
        {
            return true;
        }

        string compact = normalized.Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return compact.Equals("changeme", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("replaceme", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("placeholder", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("example", StringComparison.OrdinalIgnoreCase)
            || compact.Equals("yourapikey", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("replace-with-", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("replace_with_", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("synthetic-", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("test-", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("development-", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateSection(string section, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException
            or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Configuration binder의 원문 값/inner exception에도 주소나 자격증명이 들어갈 수 있다.
            throw InvalidConfiguration(section);
        }
    }

    private static InvalidOperationException InvalidConfiguration(string section)
    {
        return new($"{section} 송신 설정이 올바르지 않습니다. 활성화, endpoint, 자격증명 및 환경 정책을 확인하세요.");
    }
}
