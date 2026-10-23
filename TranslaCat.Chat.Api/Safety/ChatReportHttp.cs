using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Safety;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Safety;
using Microsoft.EntityFrameworkCore;

namespace TranslaCat.Chat.Api.Safety;

[ApiController, ChatReadEndpoint, ChatReportBoundary, ChatSafetyJson, Authorize(Policy = ChatReadAuthorization.Policy), RequestSizeLimit(16384)]
public sealed class ChatReportController(IChatReportStore store, ChatSafetyOptions options, ChatReadHttpResponses responses,
    ChatReadTimestampFormatter timestamps, ChatReadLocalTime localTime, TimeProvider clock) : ControllerBase
{
    [HttpPost("api/v1/chat/rooms/{roomId:long}/reports")]
    public async Task<IActionResult> Create(long roomId, [FromBody] ChatReportCreateRequest request, CancellationToken token)
    {
        RequireEnabled();
        var receipt = await store.CreateAsync(UserId(), roomId, request, Now(), token);
        return Ok(responses.Success(HttpContext, Receipt(receipt)));
    }

    [Authorize(Roles = "ROLE_ADMIN"), HttpGet("api/v1/admin/chat/reports")]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] long? cursorId, [FromQuery] int? size, CancellationToken token)
    {
        RequireEnabled();
        var result = await store.ListAsync(status, cursorId, size ?? 30, Now(), token);
        return Ok(responses.Success(HttpContext, new { reports = result.Reports.Select(Receipt), result.NextCursorId, result.HasNext }));
    }

    [Authorize(Roles = "ROLE_ADMIN"), HttpGet("api/v1/admin/chat/reports/{reportId:long}")]
    public async Task<IActionResult> Get(long reportId, CancellationToken token)
    {
        RequireEnabled();
        return Ok(responses.Success(HttpContext, Detail(await store.GetAsync(reportId, Now(), token))));
    }

    [Authorize(Roles = "ROLE_ADMIN"), HttpPatch("api/v1/admin/chat/reports/{reportId:long}/status")]
    public async Task<IActionResult> Update(long reportId, [FromBody] ChatReportStatusRequest request, CancellationToken token)
    {
        RequireEnabled();
        return Ok(responses.Success(HttpContext, Detail(await store.UpdateAsync(UserId(), reportId, request, Now(), token))));
    }

    private object Receipt(ChatReportReceipt row) => new { row.Id, row.RoomId, row.TargetType, row.Reason, row.Status,
        createdAt = timestamps.Format(row.CreatedAt), evidenceExpiresAt = timestamps.Format(row.EvidenceExpiresAt),
        row.Duplicate, row.Version, row.EvidenceExpired };
    private object Detail(ChatReportDetail row) => new { row.Receipt.Id, row.Receipt.RoomId, row.Receipt.TargetType,
        row.Receipt.Reason, row.Receipt.Status, createdAt = timestamps.Format(row.Receipt.CreatedAt),
        evidenceExpiresAt = timestamps.Format(row.Receipt.EvidenceExpiresAt), row.Receipt.Duplicate, row.Receipt.Version,
        row.Receipt.EvidenceExpired, row.Detail, row.EvidenceContent, row.EvidenceSenderDisplay, row.ReviewNote,
        updatedAt = timestamps.Format(row.UpdatedAt) };
    private long UserId() => ChatReadAuthorization.TryGetUserId(User, out long id) ? id : throw new UnauthorizedAccessException();
    private DateTime Now() => localTime.FromUtc(clock.GetUtcNow());
    private void RequireEnabled()
    {
        Response.Headers.CacheControl = "no-store";
        if (!options.Enabled) throw new ChatSafetyException("CHAT_SAFETY_DISABLED", "신고 기능을 준비하고 있습니다.");
    }
}

public sealed class ChatReportBoundaryAttribute : ActionFilterAttribute, IExceptionFilter
{
    public ChatReportBoundaryAttribute() { Order = -3300; }
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        if (!context.ModelState.IsValid) context.Result = Error(context.HttpContext, 400, "CHAT_REPORT_INVALID", "신고 요청 형식을 확인해 주세요.");
    }
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested) return;
        var (status, code, message) = context.Exception switch
        {
            ChatSafetyException error => (error.Code.EndsWith("CONFLICT", StringComparison.Ordinal) ? 409 : error.Code == "CHAT_REPORT_NOT_FOUND" ? 404
                : error.Code == "CHAT_SAFETY_DISABLED" ? 503 : 400, error.Code, error.Message),
            ChatCoreUnavailableException => (503, "CHAT_SAFETY_UNAVAILABLE", "신고 서비스에 연결하지 못했습니다."),
            _ => (503, "CHAT_SAFETY_UNAVAILABLE", "신고 서비스에 연결하지 못했습니다.")
        };
        context.Result = Error(context.HttpContext, status, code, message);
        context.ExceptionHandled = true;
    }
    private static ObjectResult Error(HttpContext context, int status, string code, string message) => new(
        context.RequestServices.GetRequiredService<ChatReadHttpResponses>().Error(context, status, code, message)) { StatusCode = status };
}

public static class ChatReportHttpExtensions
{
    public static IServiceCollection AddChatReportHttp(this IServiceCollection services)
    {
        services.TryAddScoped<IChatReportStore>(provider => new EfChatReportStore(
            provider.GetRequiredService<IDbContextFactory<ChatDbContext>>(), provider.GetService<IChatMessageProfileReader>(), provider.GetService<IChatContentPolicy>()));
        services.TryAddSingleton(provider => new ChatReadTimestampFormatter(provider.GetRequiredService<ChatReadLocalTime>().SourceTimeZone));
        return services;
    }
}

public sealed class ChatReportEvidenceWorker(IServiceScopeFactory scopes, ChatReadLocalTime localTime, TimeProvider clock,
    ILogger<ChatReportEvidenceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IChatReportStore>().PurgeExpiredEvidenceAsync(localTime.FromUtc(clock.GetUtcNow()), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning("CHAT report evidence purge deferred ({FailureType}).", exception.GetType().Name); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
