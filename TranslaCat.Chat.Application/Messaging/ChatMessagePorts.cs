namespace TranslaCat.Chat.Application.Messaging;

public interface IChatMessageTransaction
{
    // Save/flush 이후에도 callback 실패나 commit 실패가 발생하면 어떤 의도도 전달하지 않는다.
    // commit 뒤 전달 실패는 기록하고 다음 독립 의도를 시도한다. 취소는 전파하며 commit을 되돌리지 않는다.
    Task<T> ExecuteAsync<T>(
        Func<IChatMessageSession, CancellationToken, Task<T>> work,
        CancellationToken cancellationToken);
}

public interface IChatMessageSession
{
    Task<ChatMessageMember> GetMemberAsync(long userId, long roomId, bool forSend, CancellationToken cancellationToken);

    // 같은 방의 SENT/nondeleted/현재 가입 시각 이후 행만 조회한다. forward=false는 ID DESC, true는 ASC다.
    Task<IReadOnlyList<ChatStoredMessage>> FetchAsync(ChatMessageMember member, long? cursorId, bool forward, int limit, CancellationToken cancellationToken);

    // anchor/after의 기준 행도 FetchAsync와 같은 접근 범위에 속해야 한다.
    Task<ChatStoredMessage?> FindAccessibleAsync(ChatMessageMember member, long messageId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatMessageView>> PresentAsync(ChatMessageMember member, IReadOnlyList<ChatStoredMessage> messages, CancellationToken cancellationToken);
    Task<ChatMessageLanguages> ResolveLanguagesAsync(ChatMessageMember member, CancellationToken cancellationToken);
    Task<ChatMessageCreation> InsertTextAsync(ChatMessageMember member, string content, IReadOnlyList<string> translationLanguages, DateTime createdAt, CancellationToken cancellationToken);
    void RegisterAfterCommit(ChatMessageIntent intent);
}

public interface IChatMessageProfileReader
{
    Task<ChatUserMessageProfile> GetUserAsync(long userId, CancellationToken cancellationToken);
    Task<string?> ResolveObjectUrlAsync(string objectKey, CancellationToken cancellationToken);
    Task<string> GetAuditIdentityAsync(long userId, CancellationToken cancellationToken);
}

public interface IChatMessageEventDelivery
{
    // 미구성된 번역/AI/실시간 경계를 성공으로 위장하지 않고 commit 전에 거절한다.
    Task ValidateAvailabilityAsync(IReadOnlyList<ChatMessageIntent> intents, CancellationToken cancellationToken);

    // 실제 commit 뒤에만 호출한다. 실패는 이미 완료된 DB commit을 취소하지 못한다.
    Task DeliverAsync(ChatMessageIntent intent, CancellationToken cancellationToken);
}

public abstract record ChatMessageIntent;
public sealed record ChatMessageCreatedIntent(ChatMessageView Message) : ChatMessageIntent;
public sealed record ChatTranslationRequestedIntent(long ChatRoomId, long MessageId, long? SenderUserId, IReadOnlyList<long> TranslationIds) : ChatMessageIntent;
public sealed record ChatHumanMessageRecordedIntent(long MessageId, long ChatRoomId, DateTime CreatedAt) : ChatMessageIntent;
public sealed record ChatAiTriggerRequestedIntent(long MessageId) : ChatMessageIntent;
