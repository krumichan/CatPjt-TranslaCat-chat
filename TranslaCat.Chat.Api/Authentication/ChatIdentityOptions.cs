using TranslaCat.Chat.Api.ServiceAuthentication;

namespace TranslaCat.Chat.Api.Authentication;

public sealed class ChatIdentityOptions
{
    public const string SectionName = "Chat:Identity";
    public bool Enabled
    {
        get; set;
    }
    public string? BaseUrl
    {
        get; set;
    }
    public int TimeoutSeconds { get; set; } = 5;
    public ChatServiceTokenSettings ServiceAuthentication { get; set; } = new();
}
