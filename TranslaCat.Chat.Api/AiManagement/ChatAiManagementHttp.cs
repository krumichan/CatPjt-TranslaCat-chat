using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.OpenMembership;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.AiManagement;

namespace TranslaCat.Chat.Api.AiManagement;

[ApiController, ChatReadEndpoint, ChatAiManagementBoundary]
[Authorize(Policy = ChatReadAuthorization.Policy)]
[Route("api/v1/chat/rooms/{roomId}/ai-members")]
public sealed class ChatAiMembersController(IChatAiManagementStore store, ChatAiManagementMapper mapper,
    ChatReadHttpResponses responses, ChatReadLocalTime local, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.List(await store.ListAsync(UserId(), roomId, Now(), token))));
    }

    [HttpGet("{aiMemberId}")]
    public async Task<IActionResult> Get([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long aiMemberId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.Member(await store.GetAsync(UserId(), roomId, aiMemberId, false, token))));
    }

    [HttpGet("{aiMemberId}/profile")]
    public async Task<IActionResult> Profile([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long aiMemberId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.Safe(await store.GetAsync(UserId(), roomId, aiMemberId, true, token))));
    }

    [HttpPost]
    public async Task<IActionResult> Create([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [FromBody] ChatAiProfileDto request, CancellationToken token)
    {
        var member = await store.CreateAsync(UserId(), roomId, request.ToInput(), Now(), token);
        // 원본 ResponseUtil.created는 HTTP 200에 envelope 201을 담는다.
        return Ok(responses.Success(HttpContext, mapper.Member(member)) with
        {
            ResultCode = 201,
            Message = "Created"
        });
    }

    [HttpPatch("{aiMemberId}")]
    public async Task<IActionResult> Update([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long aiMemberId, [FromBody] ChatAiProfileDto request, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.Member(await store.UpdateAsync(UserId(), roomId, aiMemberId, request.ToInput(), Now(), token))));
    }

    [HttpDelete("{aiMemberId}")]
    public async Task<IActionResult> Delete([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long aiMemberId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.Member(await store.DeleteAsync(UserId(), roomId, aiMemberId, Now(), token))));
    }

    private long UserId()
    {
        return ChatReadAuthorization.TryGetUserId(User, out long id) ? id : throw new UnauthorizedAccessException();
    }

    private DateTime Now()
    {
        return local.FromUtc(clock.GetUtcNow());
    }
}

[ApiController, ChatReadEndpoint, ChatAiManagementBoundary]
[Authorize(Policy = ChatReadAuthorization.Policy)]
[Route("api/v1/chat/rooms/{roomId}/ai-settings")]
public sealed class ChatAiRoomSettingsController(IChatAiManagementStore store, ChatReadHttpResponses responses,
    ChatReadLocalTime local, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, await store.RoomSettingsAsync(UserId(), roomId, null, Now(), token)));
    }

    [HttpPatch]
    public async Task<IActionResult> Update([ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [FromBody] ChatAiRoomPatchDto request, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, await store.RoomSettingsAsync(UserId(), roomId, request.ToPatch(), Now(), token)));
    }

    private long UserId()
    {
        return ChatReadAuthorization.TryGetUserId(User, out long id) ? id : throw new UnauthorizedAccessException();
    }

    private DateTime Now()
    {
        return local.FromUtc(clock.GetUtcNow());
    }
}

[ApiController, ChatReadEndpoint, ChatAiManagementBoundary]
[Authorize(Policy = ChatReadAuthorization.Policy, Roles = "ROLE_ADMIN")]
[Route("api/v1/admin/chat/ai-settings")]
public sealed class ChatAiSystemSettingsController(IChatAiManagementStore store, ChatAiManagementMapper mapper,
    ChatReadHttpResponses responses, ChatReadLocalTime local, TimeProvider clock) : ControllerBase
{
    // 관리자 여부는 JWT 입력 claim이 아니라 현재 계정 resolver가 확인한 role에 의해 결정된다.
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.SystemSettings(await store.SystemSettingsAsync(UserId(), null, Now(), token))));
    }

    [HttpPatch]
    public async Task<IActionResult> Update([FromBody] ChatAiSystemPatchDto request, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, mapper.SystemSettings(await store.SystemSettingsAsync(UserId(), request.ToPatch(), Now(), token))));
    }

    private long UserId()
    {
        return ChatReadAuthorization.TryGetUserId(User, out long id) ? id : throw new UnauthorizedAccessException();
    }

    private DateTime Now()
    {
        return local.FromUtc(clock.GetUtcNow());
    }
}

public static class ChatAiManagementExtensions
{
    public static IServiceCollection AddChatAiManagementHttp(this IServiceCollection services)
    {
        services.AddSingleton<ChatAiManagementMapper>();
        services.AddScoped(provider => new EfChatAiManagementStore(
            provider.GetService<IDbContextFactory<ChatDbContext>>() ?? throw new ChatAiManagementUnavailableException(),
            provider.GetRequiredService<IOpenMembershipDelivery>(), provider.GetRequiredService<ILogger<EfChatAiManagementStore>>(),
            provider.GetService<IChatRoomAccountReader>(), provider.GetService<IChatAiProfileStorage>()));
        services.AddScoped<IChatAiManagementStore>(provider => provider.GetRequiredService<EfChatAiManagementStore>());
        return services;
    }
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class ChatAiManagementBoundaryAttribute : ActionFilterAttribute, IExceptionFilter
{
    public ChatAiManagementBoundaryAttribute()
    {
        Order = -3200;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            context.Result = Error(context.HttpContext, 500, "", "AI 채팅 요청 형식 또는 입력 값이 올바르지 않습니다.");
        }
    }

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            return;
        }

        var (status, code, message) = context.Exception switch
        {
            ChatAiManagementException business => (400, business.Code, business.Message),
            ChatAiManagementUnavailableException or ChatRoomDependencyUnavailableException or OpenRoomDependencyUnavailableException =>
                (503, "CHAT_AI_UNAVAILABLE", "AI 채팅 의존성이 구성되지 않았습니다."),
            _ => (500, "", "AI 채팅 요청을 처리하지 못했습니다.")
        };
        context.Result = Error(context.HttpContext, status, code, message);
        context.ExceptionHandled = true;
    }

    private static ObjectResult Error(HttpContext context, int status, string code, string message)
    {
        return new(context.RequestServices.GetRequiredService<ChatReadHttpResponses>().Error(context, status, code, $"Message <{message}>"))
        {
            StatusCode = status
        };
    }
}
