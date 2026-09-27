using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Read;

namespace TranslaCat.Chat.Api.Messaging;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ChatMessageHttpBoundaryAttribute : ActionFilterAttribute, IExceptionFilter
{
    public ChatMessageHttpBoundaryAttribute()
    {
        Order = -3200;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            // BE 전역 Advice catch-all의 HTTP500을 유지한다. 400으로 공개 계약을 임의 정규화하지 않는다.
            context.Result = Error(context.HttpContext, 500, "", "메시지 요청 형식 또는 입력 값이 올바르지 않습니다.");
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
            ChatMessageException business => (400, business.ErrorCode, business.Message),
            ChatReadException access => (400, access.Code, access.Message),
            ChatMessageDependencyUnavailableException => (503, "CHAT_MESSAGE_UNAVAILABLE", "메시지 처리 의존성이 구성되지 않았습니다."),
            _ => (500, "", "메시지 요청을 처리하지 못했습니다.")
        };

        // framework/DB 예외나 profile credential을 응답에 노출하지 않는다.
        context.Result = Error(context.HttpContext, status, code, message);
        context.ExceptionHandled = true;
    }

    private static ObjectResult Error(HttpContext context, int status, string code, string message)
    {
        return new(context.RequestServices.GetRequiredService<ChatReadHttpResponses>()
                .Error(context, status, code, $"Message <{message}>"))
        {
            StatusCode = status
        };
    }
}
