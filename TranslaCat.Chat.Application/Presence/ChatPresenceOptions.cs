namespace TranslaCat.Chat.Application.Presence;

public sealed record ChatPresenceOptions
{
    public bool Enabled
    {
        get; init;
    }
    public TimeSpan SessionTtl { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan OfflineGrace { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan TransitionStateTtl => SessionTtl + OfflineGrace + RefreshInterval;

    public void Validate()
    {
        // Redis의 밀리초 lease와 worker 주기가 모두 유효한지 시작 전에 확인한다.
        if (SessionTtl.TotalMilliseconds < 1 || RefreshInterval.TotalMilliseconds < 1)
        {
            throw new ArgumentException("Presence TTL and refresh interval must be at least one millisecond.");
        }

        if (RefreshInterval >= SessionTtl || OfflineGrace < TimeSpan.Zero)
        {
            throw new ArgumentException("Presence refresh must be shorter than TTL; grace must not be negative.");
        }
    }
}
