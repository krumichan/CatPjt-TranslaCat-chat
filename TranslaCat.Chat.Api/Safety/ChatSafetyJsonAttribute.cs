using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TranslaCat.Chat.Api.Safety;

public sealed class ChatSafetyJsonAttribute : Attribute, IAsyncResourceFilter, IOrderedFilter
{
    public int Order => -4000;
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (HttpMethods.IsGet(request.Method)) { await next(); return; }
        request.EnableBuffering(16 * 1024, 16 * 1024);
        try
        {
            // 제한된 JSON을 한 번 확인하고 model binder가 같은 원문을 읽게 되돌린다.
            using var document = await JsonDocument.ParseAsync(request.Body, new() { MaxDepth = 8 }, context.HttpContext.RequestAborted);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !Unique(document.RootElement)) throw new JsonException();
            request.Body.Position = 0;
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.Result = new BadRequestObjectResult(new { code = "CHAT_SAFETY_INVALID_JSON" });
            return;
        }
        await next();
    }
    private static bool Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            return value.EnumerateObject().All(property => names.Add(property.Name) && Unique(property.Value));
        }
        return value.ValueKind != JsonValueKind.Array || value.EnumerateArray().All(Unique);
    }
}
