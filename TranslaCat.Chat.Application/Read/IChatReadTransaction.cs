namespace TranslaCat.Chat.Application.Read;

public interface IChatReadTransaction
{
    // 호출마다 독립 session을 만들고 작업, 실제 commit, 등록 이벤트 인계를 순서대로 수행한다.
    // callback/save 완료는 commit이 아니다. commit 전 실패/취소/rollback이면 상태와 의도를 폐기한다.
    // 성공 응답은 commit 확인 뒤에만 반환한다. 외부 전달은 commit 성공 이후에만 허용한다.
    // commit 후 전달 실패는 rollback으로 취급하지 않으며, 기록 후 나머지 독립 의도를 시도한다.
    // 호출자 취소는 성공 응답으로 바꾸지 않는다. commit 후 취소도 전파하되 이미 commit된 상태를 되돌리지 않는다.
    // 재시도/내구성/exactly-once는 보장하지 않는다. 실제 보장은 연결한 adapter의 검증 범위로 구분한다.
    Task<ChatRoomReadResponse> ExecuteAsync(
        Func<IChatReadSession, CancellationToken, Task<ChatRoomReadResponse>> work,
        CancellationToken cancellationToken);
}
