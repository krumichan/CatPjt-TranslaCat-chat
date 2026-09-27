using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TranslaCat.Chat.Api.Read;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ChatReadInputValidationAttribute : ActionFilterAttribute
{
    public ChatReadInputValidationAttribute()
    {
        // MVC의 UnsupportedContentTypeFilter보다 먼저 binding 오류를 같은 BE envelope로 처리한다.
        Order = -3100;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ModelState.IsValid)
        {
            return;
        }

        var responses = context.HttpContext.RequestServices.GetRequiredService<ChatReadHttpResponses>();
        context.Result = new ObjectResult(responses.Error(
            context.HttpContext,
            500,
            "",
            "Message <읽음 요청 형식 또는 입력 값이 올바르지 않습니다.>"))
        {
            StatusCode = 500
        };
    }
}
