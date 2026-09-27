namespace TranslaCat.Chat.Api.Authentication;

public sealed class ChatJwtOptions
{
    public const string SectionName = "Chat:Authentication";

    public bool Enabled
    {
        get; set;
    }

    // BE jwt.token.secret-key와 같은 Base64 key를 비밀 설정으로 전달한다.
    public string? Base64SigningKey
    {
        get; set;
    }
}
