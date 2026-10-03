using TranslaCat.Chat.Api.Configuration;
using TranslaCat.Chat.Api.Core;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Realtime;
using TranslaCat.Chat.Api.Runtime;
using TranslaCat.Chat.Api.ServiceAuthentication;

ChatConfiguration.ValidateEnvironment(
    Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
    Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));
if (args.Contains("--migrate-database", StringComparer.Ordinal))
{
    Environment.ExitCode = await TranslaCat.Chat.Api.Runtime.ChatMigrationCommand.RunAsync();
    return;
}
var validateConfiguration = args.Contains("--validate-configuration", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(value => value != "--validate-configuration").ToArray());
ChatConfiguration.AddSecretFiles(builder.Configuration);
ChatConfiguration.Validate(builder.Configuration, builder.Environment.EnvironmentName);

// 읽음 transport와 기존 Health Controller를 같은 실제 파이프라인에 연결한다.
builder.Services.AddChatReadHttp();
builder.Services.AddChatRuntime(builder.Configuration);
builder.Services.AddChatServiceAuthentication(builder.Configuration);
builder.Services.AddChatCore(builder.Configuration, builder.Environment.EnvironmentName);
builder.Services.AddOpenApi();

// 실제 provider와 Options 등록만 검사한다. host/worker/DB/Redis 연결은 시작하지 않는다.
if (validateConfiguration)
{
    Console.WriteLine("VERIFIED: CHAT configuration binding. No host or external connection was started.");
    return;
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseChatReadHttp();
app.MapChatRealtime();
app.MapChatReadiness();

app.Run();
