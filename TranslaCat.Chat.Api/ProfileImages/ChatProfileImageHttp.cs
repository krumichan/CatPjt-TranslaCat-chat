using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TranslaCat.Chat.Api.AiManagement;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Api.OpenRooms;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.ProfileImages;
using TranslaCat.Chat.Application.Rooms;

namespace TranslaCat.Chat.Api.ProfileImages;

[ApiController, ChatReadEndpoint, ChatProfileImageBoundary, Authorize(Policy = ChatReadAuthorization.Policy)]
[RequestSizeLimit(12 * 1024 * 1024), RequestFormLimits(MultipartBodyLengthLimit = 10 * 1024 * 1024)]
public sealed class ChatProfileImagesController(IChatAiImageStore ai, IOpenProfileImageStore open,
    ChatAiManagementMapper aiMapper, OpenRoomContractMapper openMapper, ChatReadHttpResponses responses,
    ChatReadLocalTime local, TimeProvider clock) : ControllerBase
{
    [HttpPost("/api/v1/chat/open-rooms/{roomId}/me/profile-image")]
    public async Task<IActionResult> UploadOpen([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId, CancellationToken token)
    {
        var upload = await ReadUploadAsync(false, token);
        return Ok(responses.Success(HttpContext, openMapper.Profile(await open.UploadImageAsync(UserId(), roomId, upload, Now(), token))));
    }

    [HttpPost("/api/v1/chat/rooms/{roomId}/ai-members/{aiMemberId}/profile-image")]
    public Task<IActionResult> UploadAiProfile([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long aiMemberId, CancellationToken token)
    {
        return UploadAiAsync(roomId, aiMemberId, ChatProfileImageKind.Profile, token);
    }

    [HttpPost("/api/v1/chat/rooms/{roomId}/ai-members/{aiMemberId}/profile-background-image")]
    public Task<IActionResult> UploadAiBackground([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long aiMemberId, CancellationToken token)
    {
        return UploadAiAsync(roomId, aiMemberId, ChatProfileImageKind.Background, token);
    }

    [HttpDelete("/api/v1/chat/rooms/{roomId}/ai-members/{aiMemberId}/profile-image")]
    public Task<IActionResult> DeleteAiProfile([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long aiMemberId, CancellationToken token)
    {
        return DeleteAiAsync(roomId, aiMemberId, ChatProfileImageKind.Profile, token);
    }

    [HttpDelete("/api/v1/chat/rooms/{roomId}/ai-members/{aiMemberId}/profile-background-image")]
    public Task<IActionResult> DeleteAiBackground([FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long roomId,
        [FromRoute, ModelBinder(BinderType = typeof(ChatMessageNumberBinder))] long aiMemberId, CancellationToken token)
    {
        return DeleteAiAsync(roomId, aiMemberId, ChatProfileImageKind.Background, token);
    }

    private async Task<IActionResult> UploadAiAsync(long roomId, long memberId, ChatProfileImageKind kind, CancellationToken token)
    {
        var upload = await ReadUploadAsync(true, token);
        return Ok(responses.Success(HttpContext, aiMapper.Member(await ai.UploadImageAsync(UserId(), roomId, memberId, kind, upload, Now(), token))));
    }

    private async Task<IActionResult> DeleteAiAsync(long roomId, long memberId, ChatProfileImageKind kind, CancellationToken token)
    {
        return Ok(responses.Success(HttpContext, aiMapper.Member(await ai.DeleteImageAsync(UserId(), roomId, memberId, kind, Now(), token))));
    }

    private async Task<ChatProfileImageUpload> ReadUploadAsync(bool aiImage, CancellationToken token)
    {
        // 실제 framework multipart parser를 거친다. 원본 catch-all 500을 유지하며 임의 415로 바꾸지 않는다.
        if (!Request.HasFormContentType || Request.ContentLength > 12L * 1024 * 1024)
        {
            throw new InvalidDataException("Invalid multipart image request.");
        }
        var form = await Request.ReadFormAsync(token);
        var file = form.Files.GetFile("file") ?? throw new InvalidDataException("Required file part is missing.");
        try
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, token);
            return new(file.ContentType, buffer.ToArray());
        }
        catch (IOException)
        {
            throw new ChatProfileImageException(aiImage ? "AI 프로필 이미지 파일을 읽을 수 없습니다." : "OPEN 프로필 이미지 파일을 읽을 수 없습니다.",
                aiImage ? "CHAT_AI_PROFILE_IMAGE_READ_FAILED" : "OPEN_CHAT_PROFILE_IMAGE_READ_FAILED");
        }
    }

    private long UserId()
    {
        return ChatReadAuthorization.TryGetUserId(User, out var id) ? id : throw new UnauthorizedAccessException();
    }

    private DateTime Now()
    {
        return local.FromUtc(clock.GetUtcNow());
    }
}

public sealed class ChatProfileImageBoundaryAttribute : ActionFilterAttribute, IExceptionFilter
{
    public ChatProfileImageBoundaryAttribute()
    {
        Order = -3200;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            context.Result = Error(context.HttpContext, 500, "", "이미지 요청 형식이 올바르지 않습니다.");
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
            ChatProfileImageException business => (400, business.Code, business.Message),
            ChatAiManagementException business => (400, business.Code, business.Message),
            OpenRoomException business => (400, business.Code, business.Message),
            ChatProfileImageUnavailableException or ChatAiManagementUnavailableException or OpenRoomDependencyUnavailableException or ChatRoomDependencyUnavailableException
                => (503, "CHAT_PROFILE_IMAGE_UNAVAILABLE", "이미지 저장 의존성이 구성되지 않았습니다."),
            _ => (500, "", "이미지 요청을 처리하지 못했습니다.")
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

public static class ChatProfileImageHttpExtensions
{
    public static IServiceCollection AddChatProfileImageHttp(this IServiceCollection services)
    {
        // 기존 scoped store의 실제 EF 인스턴스를 그대로 공유하고 테스트/운영 가짜 storage는 등록하지 않는다.
        services.TryAddScoped<IChatAiImageStore>(provider => provider.GetRequiredService<IChatAiManagementStore>() as IChatAiImageStore
            ?? throw new ChatProfileImageUnavailableException());
        services.TryAddScoped<IOpenProfileImageStore>(provider => provider.GetRequiredService<IOpenRoomStore>() as IOpenProfileImageStore
            ?? throw new ChatProfileImageUnavailableException());
        return services;
    }
}
