namespace TranslaCat.Chat.Application.Realtime;

public interface IChatRealtimeAccess
{
    // 구독과 실제 방 이벤트 전달 시 현재 접근 권한을 다시 확인한다.
    Task ValidateRoomAccessAsync(long userId, long roomId, CancellationToken cancellationToken);

    // 종료 안내만 기존 활성 회원에게 전달한다. 신규 구독/메시지 접근에는 이 검사를 사용하지 않는다.
    Task ValidateRoomClosureDeliveryAsync(long userId, long roomId, CancellationToken cancellationToken)
    {
        return ValidateRoomAccessAsync(userId, roomId, cancellationToken);
    }
}

public interface IChatRealtimeMessageSender
{
    // 저장과 commit 이후 이벤트 등록은 Application 구현이 소유한다. transport는 재발행하지 않는다.
    Task SendTextAsync(long userId, long roomId, string content, CancellationToken cancellationToken);
}

public interface IChatRealtimeSessionLifecycle
{
    Task ConnectAsync(string sessionId, long userId, CancellationToken cancellationToken);

    Task DisconnectAsync(string sessionId, long userId, CancellationToken cancellationToken);
}
