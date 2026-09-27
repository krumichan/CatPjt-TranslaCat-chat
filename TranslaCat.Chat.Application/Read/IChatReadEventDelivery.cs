namespace TranslaCat.Chat.Application.Read;

// 실제 transaction commit 이후에만 호출한다. 전송 실패가 commit을 되돌리지는 않는다.
public interface IChatReadEventDelivery
{
    Task DeliverAsync(ChatReadUpdated intent, CancellationToken cancellationToken);
    Task DeliverAsync(ChatMemberReadUpdated intent, CancellationToken cancellationToken);
}

// 공통 계정 DB를 복제하지 않고 검증된 신원 경계에서 개인 큐 recipient를 얻는다.
public interface IChatReadRecipientResolver
{
    Task<string?> ResolveUsernameAsync(long userId, CancellationToken cancellationToken);
}
