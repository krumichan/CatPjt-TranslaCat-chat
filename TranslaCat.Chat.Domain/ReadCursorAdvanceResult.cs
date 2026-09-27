namespace TranslaCat.Chat.Domain;

// Advanced는 상태 전진 여부이며, 모든 읽음 이벤트의 발행 여부를 뜻하지 않는다.
public readonly record struct ReadCursorAdvanceResult(ReadCursor Cursor, bool Advanced);
