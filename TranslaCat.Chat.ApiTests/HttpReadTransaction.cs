using System.Text.Json;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.Read;
using TranslaCat.Chat.Domain;

namespace TranslaCat.Chat.ApiTests;

// 실제 SQL/lock/인증/전송을 흉내 내지 않고 Application port 결과와 commit 시점만 제어한다.
internal sealed class HttpReadTransaction : IChatReadTransaction, IChatReadSession
{
    private readonly List<object> pending = [];
    private readonly TaskCompletionSource<bool> commitDecision = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ChatReadMember? Member
    {
        get; set;
    } = new(
        91, 41, 73, "synthetic@example.invalid", ChatReadRoomType.Direct,
        new DateTime(2026, 9, 20), new ReadCursor(100, new DateTime(2026, 9, 21)));

    public ChatReadMessage? Message { get; set; } = new(101, 41, null, true, new DateTime(2026, 9, 22));

    public long UnreadCount { get; set; } = 7;
    public long? RequestedMessageId
    {
        get; private set;
    }
    public long? RequestedRoomId
    {
        get; private set;
    }
    public long? LoginUserId
    {
        get; private set;
    }
    public ChatReadException? AccessFailure
    {
        get; set;
    }
    public bool HoldCommit
    {
        get; set;
    }
    public bool FailCommit
    {
        get; set;
    }
    public int ExecuteCount
    {
        get; private set;
    }
    public int SaveCount
    {
        get; private set;
    }
    public bool Committed
    {
        get; private set;
    }
    public bool RolledBack
    {
        get; private set;
    }
    public ReadCursor? StagedCursor
    {
        get; private set;
    }
    public ReadCursor? CommittedCursor
    {
        get; private set;
    }
    public int PendingCount => pending.Count;
    public List<string> Calls { get; } = [];
    public List<JsonElement> DeliveredPayloads { get; } = [];
    public TaskCompletionSource<bool> WorkCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> RollbackCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void AllowCommit()
    {
        commitDecision.TrySetResult(true);
    }

    public void Rollback()
    {
        commitDecision.TrySetResult(false);
    }

    public async Task<ChatRoomReadResponse> ExecuteAsync(
        Func<IChatReadSession, CancellationToken, Task<ChatRoomReadResponse>> work,
        CancellationToken cancellationToken)
    {
        ExecuteCount++;
        try
        {
            // callback·저장은 commit과 분리한다. 정책 계산과 이벤트 생성은 실제 Application이 한다.
            var response = await work(this, cancellationToken);
            WorkCompleted.TrySetResult(true);
            var shouldCommit = !HoldCommit || await commitDecision.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!shouldCommit || FailCommit)
            {
                throw new InvalidOperationException("Synthetic commit rejected.");
            }

            Committed = true;
            CommittedCursor = StagedCursor;
            Calls.Add("commit");

            // 기록용 전달은 commit 후 실제 wire mapper를 통과한다. 네트워크 publisher는 아니다.
            var mapper = new ChatReadContractMapper(TimeZoneInfo.Utc);
            var occurredAt = FixedReadTimeProvider.Now.UtcDateTime.AddSeconds(1);
            occurredAt = DateTime.SpecifyKind(occurredAt, DateTimeKind.Unspecified);
            foreach (var intent in pending)
            {
                object payload = intent switch
                {
                    ChatReadUpdated read => mapper.ToSelfEvent(read, occurredAt),
                    ChatMemberReadUpdated member => mapper.ToMemberEvent(member, occurredAt),
                    _ => throw new InvalidOperationException("Unexpected synthetic event.")
                };
                DeliveredPayloads.Add(JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }

            pending.Clear();
            return response;
        }
        catch
        {
            if (!Committed)
            {
                RolledBack = true;
                pending.Clear();
                StagedCursor = null;
                RollbackCompleted.TrySetResult(true);
            }

            throw;
        }
    }

    public Task ValidateOpenRoomMemberAccessAsync(long loginUserId, long chatRoomId, CancellationToken cancellationToken)
    {
        Calls.Add("access");
        LoginUserId = loginUserId;
        RequestedRoomId = chatRoomId;
        if (AccessFailure is not null)
        {
            throw AccessFailure;
        }

        return Task.CompletedTask;
    }

    public Task<ChatReadMember?> FindActiveMemberForUpdateAsync(long chatRoomId, long loginUserId, CancellationToken cancellationToken)
    {
        Calls.Add("member");
        return Task.FromResult(Member);
    }

    public Task<ChatReadMessage?> FindMessageAsync(long chatRoomId, long messageId, CancellationToken cancellationToken)
    {
        Calls.Add("message");
        RequestedMessageId = messageId;
        return Task.FromResult(Message);
    }

    public Task SaveAndFlushAsync(ChatReadMember member, ReadCursor cursor, CancellationToken cancellationToken)
    {
        Calls.Add("save");
        SaveCount++;
        StagedCursor = cursor;
        return Task.CompletedTask;
    }

    public Task<long> CountUnreadAsync(long loginUserId, long chatRoomId, CancellationToken cancellationToken)
    {
        Calls.Add("unread");
        return Task.FromResult(UnreadCount);
    }

    public void RegisterAfterCommit(ChatReadUpdated readUpdated)
    {
        Calls.Add("self-intent");
        pending.Add(readUpdated);
    }

    public void RegisterAfterCommit(ChatMemberReadUpdated memberReadUpdated)
    {
        Calls.Add("member-intent");
        pending.Add(memberReadUpdated);
    }
}
