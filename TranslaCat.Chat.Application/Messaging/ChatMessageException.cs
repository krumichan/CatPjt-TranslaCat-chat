namespace TranslaCat.Chat.Application.Messaging;

public sealed class ChatMessageException(string message, string errorCode = "") : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

// 로컬 대역으로 실제 계정/스토리지/추론 연결을 대신하지 않는다.
public sealed class ChatMessageDependencyUnavailableException(string dependency)
    : Exception($"채팅 메시지 의존성이 구성되지 않았습니다: {dependency}");
