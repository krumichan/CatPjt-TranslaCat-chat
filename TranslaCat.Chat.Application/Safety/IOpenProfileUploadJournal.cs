namespace TranslaCat.Chat.Application.Safety;

// 호출자는 자신의 계정 gate를 가진다. journal은 별도 commit으로 PUT dispatch보다 먼저 살아남아야 한다.
public interface IOpenProfileUploadJournal
{
    Task StartAsync(long userId, long memberId, string newKey, string? previousKey, CancellationToken token);
    Task TrackExistingAsync(long userId, long memberId, string key, CancellationToken token);
    Task ConfirmPutAsync(long userId, string key, CancellationToken token);
    Task MarkUncertainAsync(long userId, string key, CancellationToken token);
    Task ProcessDueAsync(long? userId, CancellationToken token);
}
