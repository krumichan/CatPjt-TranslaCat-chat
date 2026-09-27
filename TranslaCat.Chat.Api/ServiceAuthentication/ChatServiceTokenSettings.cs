namespace TranslaCat.Chat.Api.ServiceAuthentication;

public class ChatServiceTokenSettings
{
    public string? Issuer
    {
        get; set;
    }
    public string? Audience
    {
        get; set;
    }
    public string? Service
    {
        get; set;
    }
    public string? Base64SigningKey
    {
        get; set;
    }
}

public sealed class ChatServiceIngressOptions : ChatServiceTokenSettings
{
    public const string SectionName = "Chat:ServiceAuthentication:Ingress";

    // 비활성 설정은 운영 경로의 인증 우회가 아니다. Testing에서만 기존 격리 fixture를 허용한다.
    public bool Enabled
    {
        get; set;
    }
}

public sealed record ChatVerifiedServiceIdentity(long UserId, IReadOnlyList<string> Scopes);
