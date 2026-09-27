using TranslaCat.Chat.Domain;

namespace TranslaCat.Chat.Application.Read;

// 모든 조회/저장/등록은 ExecuteAsync가 소유한 같은 transaction에 참여해야 한다.
public interface IChatReadSession
{
    // 원본 OPEN 접근 경계: OPEN 정보 없음은 통과, 있으면 ban -> 활성 member -> OPEN 유형 검사.
    // 거부 시 원본 OPEN_CHAT_* 실패를 전달한다. 이 읽음 경로에 closed 검사나 ID fallback을 추가하지 않는다.
    Task ValidateOpenRoomMemberAccessAsync(long loginUserId, long chatRoomId, CancellationToken cancellationToken);

    // room/user 일치, active=true, deletedAt=null만 반환하며 배타 lock은 commit/rollback까지 유지한다.
    // 부재는 null이다. 이 계약의 실제 DB 구현과 경쟁 조건 검증은 후속 adapter 작업이다.
    Task<ChatReadMember?> FindActiveMemberForUpdateAsync(long chatRoomId, long loginUserId, CancellationToken cancellationToken);

    // 요청한 ID의 snapshot을 조회한다. room 소속/삭제/SENT/가입 시점은 use case도 검증한다.
    Task<ChatReadMessage?> FindMessageAsync(long chatRoomId, long messageId, CancellationToken cancellationToken);

    // 읽음 상태를 저장·flush해 뒤따르는 집계가 볼 수 있게 한다. 이 메서드만으로 commit하지 않는다.
    Task SaveAndFlushAsync(ChatReadMember member, ReadCursor cursor, CancellationToken cancellationToken);

    // 원본 room unread 집계에 해당한다. SQL 규칙/정밀도는 adapter 책임이며 use case는 반환값을 보존한다.
    Task<long> CountUnreadAsync(long loginUserId, long chatRoomId, CancellationToken cancellationToken);

    // 불변 snapshot을 현재 transaction에 등록할 뿐, 지금 전송하지 않는다.
    // rollback이면 폐기하며 commit 후에도 개인 destination null/blank를 공개 채널로 대체하지 않는다.
    // 실제 publisher의 대상 누락 검사, occurredAt, JSON/STOMP 변환은 후속 전달 adapter가 맡는다.
    void RegisterAfterCommit(ChatReadUpdated readUpdated);
    void RegisterAfterCommit(ChatMemberReadUpdated memberReadUpdated);
}
