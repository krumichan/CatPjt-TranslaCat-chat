using TranslaCat.Chat.Application.Presence;
using TranslaCat.Chat.Application.Realtime;

namespace TranslaCat.Chat.Api.Presence;

public sealed class ChatPresenceSessionLifecycle(ChatPresenceCoordinator coordinator) : IChatRealtimeSessionLifecycle
{
    public Task ConnectAsync(string sessionId, long userId, CancellationToken cancellationToken)
    {
        // 이 진입점은 STOMP 인증 완료 뒤 transport가 호출한다. 사용자 ID를 요청 body에서 받지 않는다.
        return coordinator.ConnectedAsync(userId, sessionId, cancellationToken);
    }

    public Task DisconnectAsync(string sessionId, long userId, CancellationToken cancellationToken)
    {
        // 종료 시 principal 유무와 관계없이 등록 당시 local session 소유자를 사용한다.
        return coordinator.DisconnectedAsync(sessionId, cancellationToken);
    }
}
