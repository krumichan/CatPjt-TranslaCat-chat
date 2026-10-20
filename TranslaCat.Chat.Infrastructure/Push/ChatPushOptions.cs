namespace TranslaCat.Chat.Infrastructure.Push;

public sealed class ChatPushOptions
{
    public bool Enabled { get; set; }
    public string? AppEnvironment { get; set; }
    public string? ProjectId { get; set; }
    public string? ExpoAccessToken { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int MaxResponseBytes { get; set; } = 262144;
    public int SweepSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 60;
    public int MaxSendAttempts { get; set; } = 5;
    public int MaxReceiptAttempts { get; set; } = 96;
    public int ReceiptDelaySeconds { get; set; } = 900;

    public void Validate()
    {
        if (!Enabled) return;

        // 환경과 프로젝트는 운영자가 명시해야 한다. 비활성 기본값으로 외부 호출을 시작하지 않는다.
        if (AppEnvironment is not ("development" or "preview" or "production")
            || !Guid.TryParseExact(ProjectId, "D", out var project) || project.ToString("D") != ProjectId
            || RequestTimeoutSeconds is < 1 or > 30 || MaxResponseBytes is < 1024 or > 1048576
            || SweepSeconds is < 1 or > 60 || BatchSize is < 1 or > 100
            || LeaseSeconds < RequestTimeoutSeconds * 3 + 10 || LeaseSeconds > 300
            || MaxSendAttempts is < 1 or > 10 || MaxReceiptAttempts is < 1 or > 100
            || ReceiptDelaySeconds is < 900 or > 3600
            || (ExpoAccessToken is not null && (ExpoAccessToken.Length is < 1 or > 4096 || ExpoAccessToken.Any(char.IsWhiteSpace))))
        {
            throw new InvalidOperationException("Chat push configuration is invalid.");
        }
    }
}
