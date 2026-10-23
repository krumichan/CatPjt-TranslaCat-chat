using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.ServiceAuthentication;
using TranslaCat.Chat.Application.Safety;
using TranslaCat.Chat.Infrastructure.Core;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Safety;

namespace TranslaCat.Chat.Api.Safety;

public sealed class ChatSafetyOptions
{
    public bool Enabled { get; set; }
    public bool AccountDeletionEnabled { get; set; }
}

[ApiController, AllowAnonymous, ChatAccountDeletionAuthentication, ChatSafetyJson, RequestSizeLimit(4096)]
public sealed class ChatAccountDeletionController(IOptions<ChatServiceIngressOptions> ingress,
    ChatSafetyOptions options, IHostEnvironment environment, TimeProvider time, IServiceProvider services) : ControllerBase
{
    [HttpGet("internal/v1/chat/accounts/{userId:long}/deletion/capabilities")]
    public async Task<IActionResult> Capabilities(long userId, CancellationToken token)
    {
        if (!await AuthorizedAsync(userId, token)) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        if (!options.AccountDeletionEnabled) return StatusCode(503, new { code = "CHAT_ACCOUNT_DELETION_DISABLED" });
        var contexts = services.GetService<IDbContextFactory<ChatDbContext>>();
        if (contexts is null || services.GetService<HttpChatCoreClient>() is null
            || services.GetService<IOpenProfileUploadJournal>() is null
            || !ChatAccountDeletionReleaseGate.NormalWriteCoverageComplete) return StatusCode(503);
        try
        {
            // 호환성 조회는 DDL/업무 변경 없이 필수 테이블을 실제로 읽을 수 있는지만 확인한다.
            await using var db = await contexts.CreateDbContextAsync(token);
            await ChatAccountDeletionSchema.ValidateAsync(db, token);
            return Ok(new { version = "chat-account-deletion-v1", ready = true });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return StatusCode(503, new { code = "CHAT_ACCOUNT_DELETION_UNAVAILABLE" }); }
    }

    [HttpPost("internal/v1/chat/accounts/{userId:long}/deletion")]
    public async Task<IActionResult> Delete(long userId, [FromBody] ChatAccountDeletionRequest request, CancellationToken token)
    {
        if (!await AuthorizedAsync(userId, token)) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        if (!options.AccountDeletionEnabled || !ChatAccountDeletionReleaseGate.NormalWriteCoverageComplete)
            return StatusCode(503, new { code = "CHAT_ACCOUNT_DELETION_DISABLED" });
        try
        {
            var store = services.GetService<IChatAccountDeletionStore>();
            if (store is null) return StatusCode(503);
            return Ok(await store.DeleteAsync(userId, request, token));
        }
        catch (ChatSafetyException error)
        {
            return StatusCode(error.Code == "CHAT_ACCOUNT_DELETION_CONFLICT" ? 409 : 400, new { error.Code });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return StatusCode(503, new { code = "CHAT_ACCOUNT_DELETION_UNAVAILABLE" }); }
    }

    private async Task<bool> AuthorizedAsync(long userId, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        var values = Request.Headers.Authorization;
        if (!ingress.Value.Enabled || values.Count != 1 || values[0] is not { } value
            || !value.StartsWith("Bearer ", StringComparison.Ordinal)) return false;

        // 사용자 JWT, 일반 chat:http 또는 다른 계정의 서비스 토큰으로 삭제 경계를 통과할 수 없다.
        var identity = await ChatServiceJwt.ValidateAsync(value[7..], ingress.Value, environment.EnvironmentName,
            "chat-account-delete", new HashSet<string> { "chat:account-delete" }, time.GetUtcNow(), token);
        return identity?.UserId == userId && identity.Scopes.SequenceEqual(["chat:account-delete"]);
    }
}

public sealed class ChatAccountDeletionAuthenticationAttribute : Attribute, IAsyncResourceFilter, IOrderedFilter
{
    public int Order => -5000;
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var options = services.GetRequiredService<IOptions<ChatServiceIngressOptions>>().Value;
        var environment = services.GetRequiredService<IHostEnvironment>();
        var time = services.GetRequiredService<TimeProvider>();
        var values = context.HttpContext.Request.Headers.Authorization;
        var rawId = context.RouteData.Values["userId"]?.ToString();
        context.HttpContext.Response.Headers.CacheControl = "no-store";

        // body binding 전에 서비스 신원을 검증한다. 잘못된/거대한 익명 JSON도 삭제 로직에 도달하지 않는다.
        if (!long.TryParse(rawId, out long userId) || userId <= 0 || !options.Enabled || values.Count != 1
            || values[0] is not { } value || !value.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            context.Result = new UnauthorizedResult();
            return;
        }
        var identity = await ChatServiceJwt.ValidateAsync(value[7..], options, environment.EnvironmentName,
            "chat-account-delete", new HashSet<string> { "chat:account-delete" }, time.GetUtcNow(), context.HttpContext.RequestAborted);
        if (identity?.UserId != userId || !identity.Scopes.SequenceEqual(["chat:account-delete"]))
        {
            context.Result = new UnauthorizedResult();
            return;
        }
        await next();
    }
}

public static class ChatSafetyHttpExtensions
{
    public static IServiceCollection AddChatSafety(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Chat:Safety").Get<ChatSafetyOptions>() ?? new();
        services.AddSingleton(options);
        services.AddChatReportHttp();
        if (!options.Enabled && !options.AccountDeletionEnabled) return services;

        services.AddScoped<IChatContentPolicy, ChatContentPolicy>();
        services.AddScoped<ChatRealtimeContentPolicy>();
        services.AddScoped<IOpenProfileUploadJournal>(provider => new EfOpenProfileUploadJournal(
            provider.GetRequiredService<IDbContextFactory<ChatDbContext>>(), provider.GetService<HttpChatCoreClient>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddHostedService<ChatOpenProfileUploadWorker>();
        services.AddScoped(provider => new EfChatAccountDeletionStore(
            provider.GetRequiredService<IDbContextFactory<ChatDbContext>>(), provider.GetService<HttpChatCoreClient>(),
            instant => provider.GetRequiredService<ChatReadLocalTime>().FromUtc(instant), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOpenProfileUploadJournal>()));
        if (ChatAccountDeletionReleaseGate.NormalWriteCoverageComplete)
        {
            services.AddScoped<IChatAccountDeletionStore>(provider => provider.GetRequiredService<EfChatAccountDeletionStore>());
            if (options.AccountDeletionEnabled) services.AddHostedService<ChatAccountDeletionWorker>();
        }
        if (options.Enabled) services.AddHostedService<ChatReportEvidenceWorker>();
        return services;
    }
}

public static class ChatAccountDeletionReleaseGate
{
    // 실제 write gate·지연 전달·개인 이미지 journal의 격리 검증이 끝났다. 운영 준비는 기본 false feature flag로 분리한다.
    public static bool NormalWriteCoverageComplete => true;
}

public static class ChatAccountDeletionSchema
{
    public static async Task ValidateAsync(ChatDbContext db, CancellationToken token)
    {
        // 전체 entity projection으로 테이블뿐 아니라 필수 컬럼 누락도 접수 전에 거절한다.
        _ = await db.ChatAccountDeletions.AsNoTracking().Take(1).ToListAsync(token);
        _ = await db.ChatAccountDeletionObjects.AsNoTracking().Take(1).ToListAsync(token);
        _ = await db.ChatAccountWriteGates.AsNoTracking().Take(1).ToListAsync(token);
        _ = await db.ChatOpenProfileUploadIntents.AsNoTracking().Take(1).ToListAsync(token);
    }
}

public sealed class ChatOpenProfileUploadWorker(IServiceScopeFactory scopes, ILogger<ChatOpenProfileUploadWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IOpenProfileUploadJournal>().ProcessDueAsync(null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("OPEN image journal cleanup deferred ({FailureType}).", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}

public sealed class ChatAccountDeletionWorker(IServiceScopeFactory scopes, ILogger<ChatAccountDeletionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<EfChatAccountDeletionStore>().ProcessObjectsAsync(null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("CHAT account deletion cleanup deferred ({FailureType}).", exception.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
