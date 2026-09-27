using System.Net;

namespace TranslaCat.Chat.Infrastructure.Ai;

public sealed class ChatModelExecutionFailure(
    HttpStatusCode statusCode, string code, bool retryable, int? retryAfterSeconds = null)
    : Exception($"AI model execution failed: {code}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}
