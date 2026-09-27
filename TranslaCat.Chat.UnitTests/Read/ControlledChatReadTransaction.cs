using TranslaCat.Chat.Application.Read;
using TranslaCat.Chat.Domain;

namespace TranslaCat.Chat.UnitTests.Read;

// 실제 DB/lock/transport 구현이 아니다. 명시한 port 결과와 commit 경계만 제어한다.
internal sealed class ControlledChatReadTransaction : IChatReadTransaction
{
    private readonly Queue<ControlledChatReadSession> sessions = new();

    public int ExecutionCount
    {
        get; private set;
    }

    public void Enqueue(ControlledChatReadSession session)
    {
        sessions.Enqueue(session);
    }

    public async Task<ChatRoomReadResponse> ExecuteAsync(
        Func<IChatReadSession, CancellationToken, Task<ChatRoomReadResponse>> work,
        CancellationToken cancellationToken)
    {
        var session = sessions.Dequeue();
        ExecutionCount++;
        session.Trace.Add("begin");
        ChatRoomReadResponse response;

        // callback와 flush가 끝나도 테스트가 commit 결과를 정하기 전에는 전달하지 않는다.
        try
        {
            response = await work(session, cancellationToken);
            session.Trace.Add("work-completed");
            session.SignalWorkCompleted();
            await session.WaitForCommitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            session.Commit();
        }
        catch
        {
            session.Rollback();
            session.SignalWorkCompleted();
            throw;
        }

        // commit 이후 전달 실패는 저장을 되돌리지 않는다. 독립 의도는 계속 시도한다.
        session.HandOffAfterCommit();
        cancellationToken.ThrowIfCancellationRequested();
        return response;
    }
}

internal sealed class ControlledChatReadSession : IChatReadSession
{
    private readonly Action accessOutcome;
    private readonly TaskCompletionSource workCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource commitDecision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<long, ChatReadMessage> messages = [];
    private readonly List<object> pending = [];
    private ReadCursor? stagedCursor;

    public ControlledChatReadSession(ChatReadMember? member, Action accessOutcome)
    {
        Member = member;
        CommittedCursor = member?.Cursor;
        this.accessOutcome = accessOutcome;
    }

    public ChatReadMember? Member
    {
        get; set;
    }
    public long UnreadCount
    {
        get; set;
    }
    public Exception? SaveFailure
    {
        get; set;
    }
    public Exception? CountFailure
    {
        get; set;
    }
    public Func<object, Exception?>? DeliveryFailure
    {
        get; set;
    }
    public Action? AfterCount
    {
        get; set;
    }
    public List<string> Trace { get; } = [];
    public List<CancellationToken> Tokens { get; } = [];
    public List<(long UserId, long RoomId)> AccessRequests { get; } = [];
    public List<(long RoomId, long UserId)> MemberRequests { get; } = [];
    public List<(long RoomId, long MessageId)> MessageRequests { get; } = [];
    public List<(long UserId, long RoomId)> CountRequests { get; } = [];
    public List<(ChatReadMember Member, ReadCursor Cursor)> Saves { get; } = [];
    public List<object> Registered { get; } = [];
    public List<object> DeliveryAttempts { get; } = [];
    public List<object> Delivered { get; } = [];
    public List<Exception> DeliveryFailures { get; } = [];
    public Task WorkCompleted => workCompleted.Task;
    public int PendingCount => pending.Count;
    public bool IsCommitted
    {
        get; private set;
    }
    public bool IsRolledBack
    {
        get; private set;
    }
    public ReadCursor? CommittedCursor
    {
        get; private set;
    }

    public void AddMessage(ChatReadMessage message)
    {
        messages[message.Id] = message;
    }

    public void AllowCommit()
    {
        commitDecision.SetResult();
    }

    public void FailCommit(Exception failure)
    {
        commitDecision.SetException(failure);
    }

    // transaction 소유자가 commit 대신 명시적으로 rollback을 선택한 상황이다.
    public void RequestRollback()
    {
        commitDecision.SetCanceled();
    }

    public Task ValidateOpenRoomMemberAccessAsync(long loginUserId, long chatRoomId, CancellationToken cancellationToken)
    {
        Record("access", cancellationToken);
        AccessRequests.Add((loginUserId, chatRoomId));

        // OPEN 판정 자체를 구현하지 않는다. fixture가 collaborator의 성공/예외를 직접 지정한다.
        accessOutcome();
        return Task.CompletedTask;
    }

    public Task<ChatReadMember?> FindActiveMemberForUpdateAsync(long chatRoomId, long loginUserId, CancellationToken cancellationToken)
    {
        Record("member", cancellationToken);
        MemberRequests.Add((chatRoomId, loginUserId));
        return Task.FromResult(Member);
    }

    public Task<ChatReadMessage?> FindMessageAsync(long chatRoomId, long messageId, CancellationToken cancellationToken)
    {
        Record("message", cancellationToken);
        MessageRequests.Add((chatRoomId, messageId));
        messages.TryGetValue(messageId, out var message);
        return Task.FromResult(message);
    }

    public Task SaveAndFlushAsync(ChatReadMember member, ReadCursor cursor, CancellationToken cancellationToken)
    {
        Record("save-flush", cancellationToken);
        Saves.Add((member, cursor));
        if (SaveFailure is not null)
        {
            throw SaveFailure;
        }

        // 정책을 재계산하지 않고 실제 Application이 전달한 값을 staging한다.
        stagedCursor = cursor;
        return Task.CompletedTask;
    }

    public Task<long> CountUnreadAsync(long loginUserId, long chatRoomId, CancellationToken cancellationToken)
    {
        Record("count", cancellationToken);
        CountRequests.Add((loginUserId, chatRoomId));
        if (CountFailure is not null)
        {
            throw CountFailure;
        }

        AfterCount?.Invoke();
        return Task.FromResult(UnreadCount);
    }

    public void RegisterAfterCommit(ChatReadUpdated readUpdated)
    {
        Register(readUpdated, "register-self");
    }

    public void RegisterAfterCommit(ChatMemberReadUpdated memberReadUpdated)
    {
        Register(memberReadUpdated, "register-room");
    }

    internal void SignalWorkCompleted()
    {
        workCompleted.TrySetResult();
    }

    internal Task WaitForCommitAsync(CancellationToken cancellationToken)
    {
        return commitDecision.Task.WaitAsync(cancellationToken);
    }

    internal void Commit()
    {
        Trace.Add("commit");
        IsCommitted = true;
        if (stagedCursor is ReadCursor cursor)
        {
            CommittedCursor = cursor;
        }
    }

    internal void Rollback()
    {
        Trace.Add("rollback");
        IsRolledBack = true;
        stagedCursor = null;
        pending.Clear();
    }

    internal void HandOffAfterCommit()
    {
        foreach (var intent in pending)
        {
            var kind = intent is ChatReadUpdated ? "self" : "room";
            Trace.Add($"deliver-{kind}");
            DeliveryAttempts.Add(intent);
            var failure = DeliveryFailure?.Invoke(intent);
            if (failure is not null)
            {
                DeliveryFailures.Add(failure);
                Trace.Add($"delivery-failed-{kind}");
                continue;
            }

            Delivered.Add(intent);
        }

        pending.Clear();
    }

    private void Record(string operation, CancellationToken cancellationToken)
    {
        Trace.Add(operation);
        Tokens.Add(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void Register(object intent, string operation)
    {
        Trace.Add(operation);
        Registered.Add(intent);
        pending.Add(intent);
    }
}
