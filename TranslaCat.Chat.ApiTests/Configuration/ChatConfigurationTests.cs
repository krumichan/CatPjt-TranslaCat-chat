using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using TranslaCat.Chat.Api.Configuration;
using TranslaCat.Chat.Infrastructure.Configuration;
using TranslaCat.Chat.Infrastructure.Persistence;

namespace TranslaCat.Chat.ApiTests.Configuration;

[CollectionDefinition("Configuration environment", DisableParallelization = true)]
public sealed class ConfigurationEnvironmentCollection;

[Collection("Configuration environment")]
public sealed class ChatConfigurationTests
{
    [Theory]
    [InlineData("Chat:Ai:ApiKeyFile_FILE")]
    [InlineData("Chat:Database:Password_FILE")]
    [InlineData("Chat:Unknown_FILE")]
    public void Unregistered_file_settings_fail_without_disclosing_path(string key)
    {
        // 준비
        var configuration = new ConfigurationManager();
        configuration[key] = "private-path-marker";

        // 실행
        var error = Assert.Throws<InvalidOperationException>(() => ChatConfiguration.AddSecretFiles(configuration));

        // 검증
        Assert.Equal("Unsupported Chat secret-file setting.", error.Message);
        Assert.DoesNotContain("private-path-marker", error.ToString());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("Development", null)]
    [InlineData(null, "Production")]
    [InlineData("Production", "Production")]
    public void Supported_environment_names_are_accepted(string? dotnet, string? aspnet)
    {
        // 준비 / 실행
        var error = Record.Exception(() => ChatConfiguration.ValidateEnvironment(dotnet, aspnet));

        // 검증
        Assert.Null(error);
    }

    [Theory]
    [InlineData("Development", "Production")]
    [InlineData("Local", null)]
    [InlineData("Prod", null)]
    [InlineData("development", null)]
    [InlineData("Testing", null)]
    public void Conflicting_or_alias_environment_names_are_rejected(string dotnet, string? aspnet)
    {
        // 준비 / 실행
        var error = Assert.Throws<InvalidOperationException>(() => ChatConfiguration.ValidateEnvironment(dotnet, aspnet));

        // 검증
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Managed_file_provider_binds_the_actual_option_key_and_removes_only_terminal_newlines()
    {
        // 준비
        using var files = new PrivateFiles();
        var path = files.Write("credential", " leading-and-trailing-space \r\n");
        var configuration = new ConfigurationManager();
        configuration["Chat:Ai:ApiKey_FILE"] = path;

        // 실행
        ChatConfiguration.AddSecretFiles(configuration);

        // 검증
        Assert.Equal(" leading-and-trailing-space ", configuration["Chat:Ai:ApiKey"]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("multiline")]
    [InlineData("invalid-utf8")]
    [InlineData("relative")]
    public void Invalid_secret_files_fail_without_value_or_path_disclosure(string failure)
    {
        // 준비
        using var files = new PrivateFiles();
        var path = failure switch
        {
            "missing" => Path.Combine(files.Root, "missing"),
            "directory" => files.Root,
            "empty" => files.Write("empty", ""),
            "oversized" => files.Write("large", new string('x', ChatSecretFile.MaximumBytes + 1)),
            "multiline" => files.Write("multiline", "secret-marker\nother"),
            "invalid-utf8" => files.WriteBytes("invalid", [0xff]),
            _ => "relative/private-key"
        };

        // 실행
        var error = Assert.Throws<InvalidOperationException>(() => ChatSecretFile.Resolve(null, path, "Test:Credential"));

        // 검증
        Assert.DoesNotContain(path, error.ToString());
        Assert.DoesNotContain("secret-marker", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Value_and_file_collision_is_rejected_even_when_the_file_does_not_exist()
    {
        // 준비
        var config = new ConfigurationManager();
        config["Chat:Ai:ApiKey"] = "secret-marker";
        config["Chat:Ai:ApiKey_FILE"] = Path.Combine(Path.GetTempPath(), "missing-private-key");

        // 실행
        var error = Assert.Throws<InvalidOperationException>(() => ChatConfiguration.AddSecretFiles(config));

        // 검증
        Assert.Contains("Conflicting", error.Message);
        Assert.DoesNotContain("secret-marker", error.Message);
    }

    [Fact]
    public void Framework_configuration_order_uses_real_json_user_secrets_environment_and_command_line_providers()
    {
        // 준비: 개인 User Secrets 저장소 대신 이 테스트가 소유한 APPDATA만 사용한다.
        using var files = new PrivateFiles();
        files.Write("appsettings.json", "{\"Probe\":\"base\"}");
        files.Write("appsettings.Development.json", "{\"Probe\":\"development\"}");
        var secretsPath = Path.Combine(files.Root, "Microsoft", "UserSecrets", "translacat-chat-development");
        Directory.CreateDirectory(secretsPath);
        File.WriteAllText(Path.Combine(secretsPath, "secrets.json"), "{\"Probe\":\"user-secrets\"}");
        var oldAppData = Environment.GetEnvironmentVariable("APPDATA");
        const string name = "TRANSLACAT_CONFIG_TEST_Probe";
        var oldVariable = Environment.GetEnvironmentVariable(name);

        try
        {
            Environment.SetEnvironmentVariable("APPDATA", files.Root);
            var config = new ConfigurationManager();
            config.SetBasePath(files.Root);
            config.AddJsonFile("appsettings.json");
            Assert.Equal("base", config["Probe"]);
            config.AddJsonFile("appsettings.Development.json");
            Assert.Equal("development", config["Probe"]);

            // 실행 / 검증: 실제 UserSecrets provider와 API assembly의 UserSecretsId를 사용한다.
            config.AddUserSecrets(typeof(ChatConfiguration).Assembly, optional: false);
            Assert.Equal("user-secrets", config["Probe"]);
            Environment.SetEnvironmentVariable(name, "environment");
            config.AddEnvironmentVariables("TRANSLACAT_CONFIG_TEST_");
            Assert.Equal("environment", config["Probe"]);
            config.AddCommandLine(["--Probe=command-line"]);
            Assert.Equal("command-line", config["Probe"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", oldAppData);
            Environment.SetEnvironmentVariable(name, oldVariable);
        }
    }

    [Fact]
    public async Task Actual_host_builder_loads_development_user_secrets_but_not_production_secrets()
    {
        // 준비: 실제 WebApplication 기본 provider 순서를 개인 환경과 분리해 관측한다.
        using var files = new PrivateFiles();
        files.Write("appsettings.json", "{\"ChatConfigProbe\":\"base\"}");
        files.Write("appsettings.Development.json", "{\"ChatConfigProbe\":\"development\"}");
        files.Write("appsettings.Production.json", "{\"ChatConfigProbe\":\"production\"}");
        var secretsPath = Path.Combine(files.Root, "Microsoft", "UserSecrets", "translacat-chat-development");
        Directory.CreateDirectory(secretsPath);
        File.WriteAllText(Path.Combine(secretsPath, "secrets.json"), "{\"ChatConfigProbe\":\"user-secrets\"}");
        var oldAppData = Environment.GetEnvironmentVariable("APPDATA");
        var oldProbe = Environment.GetEnvironmentVariable("ChatConfigProbe");
        try
        {
            Environment.SetEnvironmentVariable("APPDATA", files.Root);
            Environment.SetEnvironmentVariable("ChatConfigProbe", null);

            // 실행 / 검증: 실제 app host를 만들되 서버를 열거나 runtime/DB를 등록하지 않는다.
            await AssertHostAsync("Development", [], "user-secrets");
            await AssertHostAsync("Production", [], "production");
            Environment.SetEnvironmentVariable("ChatConfigProbe", "environment");
            await AssertHostAsync("Development", [], "environment");
            await AssertHostAsync("Development", ["--ChatConfigProbe=command-line"], "command-line");
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", oldAppData);
            Environment.SetEnvironmentVariable("ChatConfigProbe", oldProbe);
        }

        async Task AssertHostAsync(string environment, string[] args, string expected)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = files.Root,
                EnvironmentName = environment,
                ApplicationName = typeof(ChatConfiguration).Assembly.GetName().Name,
                Args = args
            });
            await using var application = builder.Build();
            Assert.Equal(expected, builder.Configuration["ChatConfigProbe"]);
        }
    }

    [Fact]
    public void Production_configuration_cannot_enable_test_configuration_or_reuse_direction_credentials()
    {
        // 준비
        var config = new ConfigurationManager();
        config["Chat:Testing:Adapter"] = "fake";

        // 실행 / 검증
        Assert.Throws<InvalidOperationException>(() => ChatConfiguration.Validate(config, "Production"));
        config = new ConfigurationManager();
        config["Chat:Authentication:Base64SigningKey"] = "same-synthetic-secret";
        config["Chat:Identity:ServiceAuthentication:Base64SigningKey"] = "same-synthetic-secret";
        Assert.Throws<InvalidOperationException>(() => ChatConfiguration.Validate(config, "Development"));
    }

    [Fact]
    public void Redis_structured_configuration_contains_the_file_password_and_rejects_ambiguous_or_plaintext_input()
    {
        // 준비
        var config = new ConfigurationManager();
        config["Chat:Redis:Endpoint"] = "localhost:6379";
        config["Chat:Redis:User"] = "chat";
        config["Chat:Redis:Password"] = "synthetic-key";
        config["Chat:Redis:Namespace"] = "translacat:chat:synthetic";

        // 실행 / 검증
        Assert.Throws<InvalidOperationException>(() => ChatRedisConfiguration.GetConnectionString(config));
        config["Chat:Redis:UseTls"] = "true";
        var parsed = StackExchange.Redis.ConfigurationOptions.Parse(ChatRedisConfiguration.GetConnectionString(config)!);
        Assert.True(parsed.Ssl);
        Assert.Equal("chat", parsed.User);
        Assert.Equal("synthetic-key", parsed.Password);
        config["Chat:Redis:ConnectionString"] = "localhost:6379";
        Assert.Throws<InvalidOperationException>(() => ChatRedisConfiguration.GetConnectionString(config));
    }

    [Theory]
    [InlineData("Port=synthetic-private-marker")]
    [InlineData("synthetic-private-marker=unsupported")]
    public void Invalid_database_connection_does_not_expose_raw_values_in_errors(string invalidSetting)
    {
        // 준비: 실제 자격증명 대신 식별 가능한 합성 값으로 parser의 오류 경로를 확인한다.
        var connection = "Server=localhost;Database=translacat_chat;Password=synthetic-password;" + invalidSetting;

        // 실행
        var error = Assert.ThrowsAny<Exception>(() => ChatDatabaseTarget.Validate(connection));

        // 검증: startup/design-time의 예외에 원문과 내부 parser 예외를 남기지 않는다.
        Assert.DoesNotContain("synthetic-private-marker", error.ToString());
        Assert.DoesNotContain("synthetic-password", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("Password", "synthetic-private-marker,ssl=false")]
    [InlineData("User", "synthetic-private-marker,ssl=false")]
    [InlineData("Password", " synthetic-private-marker ")]
    [InlineData("User", " synthetic-private-marker ")]
    public void Redis_credentials_must_round_trip_exactly_or_fail_without_disclosing_input(string key, string supplied)
    {
        // 준비: structured 옵션이 문자열을 거쳐 실제 Redis client에 전달되는 경계를 검증한다.
        var config = new ConfigurationManager();
        config["Chat:Redis:Endpoint"] = "localhost:6379";
        config["Chat:Redis:User"] = "chat";
        config["Chat:Redis:Password"] = "synthetic-password";
        config["Chat:Redis:Namespace"] = "translacat:chat:synthetic";
        config["Chat:Redis:UseTls"] = "true";
        config[$"Chat:Redis:{key}"] = supplied;

        // 실행 / 검증: 거부한다면 원문 없이 거부하고, 수용한다면 credential과 TLS가 변하지 않아야 한다.
        string? serialized;
        try
        {
            serialized = ChatRedisConfiguration.GetConnectionString(config);
        }
        catch (InvalidOperationException error)
        {
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("synthetic-private-marker", error.ToString());
            return;
        }

        var parsed = StackExchange.Redis.ConfigurationOptions.Parse(serialized!);
        Assert.Equal(config["Chat:Redis:User"], parsed.User);
        Assert.Equal(config["Chat:Redis:Password"], parsed.Password);
        Assert.True(parsed.Ssl);
        Assert.Single(parsed.EndPoints);
    }

    private sealed class PrivateFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "chat-config-test-" + Guid.NewGuid().ToString("N"));

        public PrivateFiles()
        {
            Directory.CreateDirectory(Root);
        }

        public string Write(string name, string content)
        {
            return WriteBytes(name, new UTF8Encoding(false).GetBytes(content));
        }

        public string WriteBytes(string name, byte[] content)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        public void Dispose()
        {
            // 테스트가 직접 만든 실행별 임시 디렉터리만 정리한다.
            if (!Path.GetFileName(Root).StartsWith("chat-config-test-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Invalid test directory.");
            }
            Directory.Delete(Root, recursive: true);
        }
    }
}
