using TranslaCat.Chat.Application.Read;

namespace TranslaCat.Chat.Api.Read;

public sealed class ChatReadExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ChatReadHttpResponses responses)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ChatReadEndpointAttribute>() is null)
        {
            await next(context);
            return;
        }

        // 요청 취소는 오류 응답으로 위장하지 않는다. commit 여부는 Application port가 소유한다.
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var (status, code, message) = exception switch
            {
                ChatReadException business => (400, business.Code, $"Message <{business.Message}>"),
                ChatReadAdapterUnavailableException => (503, "CHAT_READ_UNAVAILABLE", "Message <읽음 저장소 연결이 구성되지 않았습니다.>"),
                _ => (500, "", "Message <읽음 요청을 처리하지 못했습니다.>")
            };

            // 내부 예외·본문·자격증명은 응답에 노출하지 않는다. BE ErrorDto의 trace는 빈 문자열이다.
            context.Response.Clear();
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(
                responses.Error(context, status, code, message),
                context.RequestAborted);
        }
    }
}

internal sealed class ChatReadAdapterUnavailableException : Exception;
