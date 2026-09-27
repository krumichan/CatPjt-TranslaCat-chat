namespace TranslaCat.Chat.Application.Read;

// 원본 업무 실패 이유를 유지한다. HTTP status/resultCode 매핑은 adapter의 별도 책임이다.
public sealed class ChatReadException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
