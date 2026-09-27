namespace TranslaCat.Chat.Api.Realtime;

public sealed class ChatRealtimeOptions
{
    public string[] AllowedOrigins { get; set; } = [];
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (ConnectTimeout < TimeSpan.FromSeconds(1) || ConnectTimeout > TimeSpan.FromSeconds(60)
            || KeepAliveInterval < TimeSpan.FromSeconds(5) || KeepAliveInterval > TimeSpan.FromMinutes(5)
            || AllowedOrigins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)
                || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
                || origin != uri.GetLeftPart(UriPartial.Authority)))
        {
            throw new InvalidOperationException("Invalid Chat realtime origin or timeout configuration.");
        }
    }
}
