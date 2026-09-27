namespace TranslaCat.Chat.Domain;

// 같은 membership의 읽음 전진만 다룬다. 재가입 등의 초기화는 별도 규칙이다.
public readonly record struct ReadCursor(long? LastReadMessageId, DateTime? LastReadAt)
{
    public ReadCursorAdvanceResult Advance(long? candidateMessageId, DateTime readAt)
    {
        // 후보가 없거나 이미 읽은 범위이면 ID와 이전 읽음 시각을 함께 보존한다.
        if (candidateMessageId is not long messageId
            || (LastReadMessageId is long currentMessageId && messageId <= currentMessageId))
        {
            return new ReadCursorAdvanceResult(this, Advanced: false);
        }

        // 실제 전진에만 호출자 시각을 적용한다. 입력 검증과 시간대 변환은 여기서 하지 않는다.
        var advancedCursor = new ReadCursor(messageId, readAt);

        return new ReadCursorAdvanceResult(advancedCursor, Advanced: true);
    }
}
