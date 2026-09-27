using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.UnitTests.Messaging;

// Application port 순서 검증용 대역이다. DB transaction/SQL/lock을 검증하는 구현이 아니다.
internal sealed class ControlledMessageTransaction : IChatMessageTransaction, IChatMessageSession
{
    public ChatMessageMember Member { get; set; } = new(7, 11, 19, "GROUP", new DateTime(2026, 9, 1));
    public ChatMessageLanguages Languages { get; set; } = new("ko", ["ko", "ja"]);
    public List<ChatMessageIntent> Registered { get; } = [];
    public List<ChatMessageIntent> Delivered { get; } = [];
    public List<(long UserId, long RoomId, bool ForSend)> AccessCalls { get; } = [];
    public List<(long? Cursor, bool Forward, int Limit)> FetchCalls { get; } = [];
    public Queue<IReadOnlyList<ChatStoredMessage>> FetchResults { get; } = [];
    public List<long> FindCalls { get; } = [];
    public ChatStoredMessage? Accessible
    {
        get; set;
    }
    public List<ChatMessageTranslationView> MutableTranslations { get; } = [];
    public Exception? AccessFailure
    {
        get; set;
    }
    public Exception? PresentationFailure
    {
        get; set;
    }
    public Exception? CommitFailure
    {
        get; set;
    }
    public TaskCompletionSource<bool>? CommitGate
    {
        get; set;
    }
    public TaskCompletionSource<bool> ReachedCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool FailFirstDelivery
    {
        get; set;
    }
    public bool Committed
    {
        get; private set;
    }
    public bool RolledBack
    {
        get; private set;
    }
    public int ExecuteCount
    {
        get; private set;
    }
    public int InsertCount
    {
        get; private set;
    }
    public int ClockIndependentLanguageCalls
    {
        get; private set;
    }
    public string? SavedContent
    {
        get; private set;
    }
    public DateTime? SavedAt
    {
        get; private set;
    }
    public IReadOnlyList<string> SavedLanguages { get; private set; } = [];
    public IReadOnlyList<ChatStoredMessage> Presented { get; private set; } = [];

    public async Task<T> ExecuteAsync<T>(Func<IChatMessageSession, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ExecuteCount++;
        try
        {
            var response = await work(this, cancellationToken);
            ReachedCommit.TrySetResult(true);
            if (CommitGate is not null)
            {
                await CommitGate.Task.WaitAsync(cancellationToken);
            }

            if (CommitFailure is not null)
            {
                throw CommitFailure;
            }

            cancellationToken.ThrowIfCancellationRequested();
            Committed = true;
            for (int index = 0; index < Registered.Count; index++)
            {
                if (!(FailFirstDelivery && index == 0))
                {
                    Delivered.Add(Registered[index]);
                }
            }

            return response;
        }
        catch
        {
            RolledBack = !Committed;
            throw;
        }
    }

    public Task<ChatMessageMember> GetMemberAsync(long userId, long roomId, bool forSend, CancellationToken cancellationToken)
    {
        AccessCalls.Add((userId, roomId, forSend));
        return AccessFailure is null ? Task.FromResult(Member) : Task.FromException<ChatMessageMember>(AccessFailure);
    }

    public Task<ChatMessageLanguages> ResolveLanguagesAsync(ChatMessageMember member, CancellationToken cancellationToken)
    {
        ClockIndependentLanguageCalls++;
        return Task.FromResult(Languages);
    }

    public Task<ChatMessageCreation> InsertTextAsync(ChatMessageMember member, string content, IReadOnlyList<string> translationLanguages, DateTime createdAt, CancellationToken cancellationToken)
    {
        InsertCount++;
        SavedContent = content;
        SavedLanguages = translationLanguages.ToArray();
        SavedAt = createdAt;
        var message = Message(9007199254740993) with
        {
            Content = content,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
        return Task.FromResult(new ChatMessageCreation(message,
            translationLanguages.Select((_, index) => 100L + index).ToArray()));
    }

    public Task<IReadOnlyList<ChatStoredMessage>> FetchAsync(ChatMessageMember member, long? cursorId, bool forward, int limit, CancellationToken cancellationToken)
    {
        FetchCalls.Add((cursorId, forward, limit));
        return Task.FromResult(FetchResults.Dequeue());
    }

    public Task<ChatStoredMessage?> FindAccessibleAsync(ChatMessageMember member, long messageId, CancellationToken cancellationToken)
    {
        FindCalls.Add(messageId);
        return Task.FromResult(Accessible);
    }

    public Task<IReadOnlyList<ChatMessageView>> PresentAsync(ChatMessageMember member, IReadOnlyList<ChatStoredMessage> messages, CancellationToken cancellationToken)
    {
        Presented = messages;
        if (PresentationFailure is not null)
        {
            return Task.FromException<IReadOnlyList<ChatMessageView>>(PresentationFailure);
        }

        return Task.FromResult<IReadOnlyList<ChatMessageView>>(messages.Select(message => new ChatMessageView(
            message.Id, message.ChatRoomId, message.SenderUserId, message.SenderAiMemberId,
            "합성 발신자", "synthetic@example.invalid", null, message.SenderType, message.MessageType,
            message.Content, message.Status, 3, MutableTranslations, message.CreatedAt, message.UpdatedAt, null)).ToArray());
    }

    public void RegisterAfterCommit(ChatMessageIntent intent)
    {
        Registered.Add(intent);
    }

    public static ChatStoredMessage Message(long id)
    {
        return new(id, 19, 11, null,
        "USER", "TEXT", "합성 본문", "SENT", new DateTime(2026, 9, 2), new DateTime(2026, 9, 2));
    }
}
