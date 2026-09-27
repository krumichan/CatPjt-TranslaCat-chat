namespace TranslaCat.Chat.Application.Ai;

public sealed class ChatExecutionDeferredException : InvalidOperationException
{
    public ChatExecutionDeferredException(string message, TimeSpan retryAfter, Exception cause)
        : base(message, cause)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan RetryAfter
    {
        get;
    }
}
