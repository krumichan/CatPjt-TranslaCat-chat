using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Realtime;

namespace TranslaCat.Chat.UnitTests.Messaging;

public sealed class ChatMessageServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 26, 12, 3, 4);

    [Fact]
    public async Task Text_creation_preserves_id_time_translation_targets_and_event_order()
    {
        // 준비
        var transaction = new ControlledMessageTransaction
        {
            Languages = new("KO", ["ko", "KO", "ja", " JA ", null, " ", "en", "ja"])
        };
        var service = new ChatMessageService(transaction, () => Now);

        // 실행
        var result = await service.CreateTextAsync(11, 19, " \t합성 메시지 😺\r\n");

        // 검증
        Assert.Equal("합성 메시지 😺", transaction.SavedContent);
        Assert.Equal(["ja", "en"], transaction.SavedLanguages);
        Assert.Equal(Now, transaction.SavedAt);
        Assert.Equal(9007199254740993, result.Id);
        Assert.Equal(3, result.UnreadMemberCount);
        Assert.Equal([(11L, 19L, true)], transaction.AccessCalls);
        Assert.Collection(transaction.Delivered,
            value => Assert.Equal(result, Assert.IsType<ChatMessageCreatedIntent>(value).Message),
            value => Assert.Equal([100L, 101L], Assert.IsType<ChatTranslationRequestedIntent>(value).TranslationIds),
            value => Assert.Equal(Now, Assert.IsType<ChatHumanMessageRecordedIntent>(value).CreatedAt),
            value => Assert.Equal(result.Id, Assert.IsType<ChatAiTriggerRequestedIntent>(value).MessageId));
    }

    [Theory]
    [InlineData("DIRECT", 1)]
    [InlineData("GROUP", 3)]
    [InlineData("OPEN", 3)]
    public async Task No_translation_and_room_type_control_independent_intents(string roomType, int eventCount)
    {
        // 준비
        var transaction = new ControlledMessageTransaction { Languages = new("ko", ["ko"]) };
        transaction.Member = transaction.Member with
        {
            RoomType = roomType
        };

        // 실행
        await new ChatMessageService(transaction, () => Now).CreateTextAsync(11, 19, "text");

        // 검증
        Assert.Equal(eventCount, transaction.Registered.Count);
        Assert.DoesNotContain(transaction.Registered, value => value is ChatTranslationRequestedIntent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n\0")]
    public async Task Empty_source_trim_input_fails_before_transaction(string? content)
    {
        // 준비
        var transaction = new ControlledMessageTransaction();

        // 실행
        var error = await Assert.ThrowsAsync<ChatMessageException>(() => new ChatMessageService(transaction, () => Now)
            .CreateTextAsync(11, 19, content));

        // 검증
        Assert.Equal("", error.ErrorCode);
        Assert.Equal(0, transaction.ExecuteCount);
    }

    [Fact]
    public async Task Service_uses_trimmed_utf16_length_and_preserves_nonbreaking_space()
    {
        // 준비
        var transaction = new ControlledMessageTransaction();
        string content = "\t" + new string('a', 4999) + "\u00a0\r";

        // 실행
        await new ChatMessageService(transaction, () => Now).CreateTextAsync(11, 19, content);

        // 검증
        Assert.Equal(5000, transaction.SavedContent!.Length);
        Assert.EndsWith("\u00a0", transaction.SavedContent);
    }

    [Fact]
    public async Task Overlong_content_fails_before_transaction()
    {
        // 준비
        var transaction = new ControlledMessageTransaction();

        // 실행
        await Assert.ThrowsAsync<ChatMessageException>(() => new ChatMessageService(transaction, () => Now)
            .CreateTextAsync(11, 19, new string('a', 5001)));

        // 검증
        Assert.Equal(0, transaction.ExecuteCount);
    }

    [Fact]
    public async Task Membership_failure_is_preserved_without_save_or_intents()
    {
        // 준비
        var failure = new ChatMessageException("접근 불가", "CHAT_ROOM_MEMBER_ACCESS_DENIED");
        var transaction = new ControlledMessageTransaction { AccessFailure = failure };

        // 실행
        var actual = await Assert.ThrowsAsync<ChatMessageException>(() => new ChatMessageService(transaction, () => Now)
            .CreateTextAsync(11, 19, "text"));

        // 검증
        Assert.Same(failure, actual);
        Assert.Equal(0, transaction.InsertCount);
        Assert.Empty(transaction.Registered);
    }

    [Fact]
    public async Task Commit_gate_prevents_event_delivery_after_save_and_before_commit()
    {
        // 준비
        var transaction = new ControlledMessageTransaction { CommitGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };

        // 실행
        var pending = new ChatMessageService(transaction, () => Now).CreateTextAsync(11, 19, "text");
        await transaction.ReachedCommit.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 검증 — fake가 제어하는 commit 이전 경계이며 실제 DB 검증은 별도 통합 테스트다.
        Assert.Equal(1, transaction.InsertCount);
        Assert.NotEmpty(transaction.Registered);
        Assert.Empty(transaction.Delivered);
        Assert.False(pending.IsCompleted);
        transaction.CommitGate.SetResult(true);
        await pending;
        Assert.True(transaction.Committed);
        Assert.Equal(transaction.Registered.Count, transaction.Delivered.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Presentation_or_commit_failure_never_delivers(bool failCommit)
    {
        // 준비
        var transaction = new ControlledMessageTransaction();
        if (failCommit)
        {
            transaction.CommitFailure = new InvalidOperationException("synthetic commit");
        }
        else
        {
            transaction.PresentationFailure = new ChatMessageDependencyUnavailableException("synthetic profile");
        }

        // 실행
        await Assert.ThrowsAnyAsync<Exception>(() => new ChatMessageService(transaction, () => Now).CreateTextAsync(11, 19, "text"));

        // 검증
        Assert.Equal(1, transaction.InsertCount);
        Assert.True(transaction.RolledBack);
        Assert.False(transaction.Committed);
        Assert.Empty(transaction.Delivered);
    }

    [Fact]
    public async Task Cancellation_waiting_for_commit_does_not_deliver()
    {
        // 준비
        using var cancellation = new CancellationTokenSource();
        var transaction = new ControlledMessageTransaction { CommitGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var pending = new ChatMessageService(transaction, () => Now).CreateTextAsync(11, 19, "text", cancellation.Token);
        await transaction.ReachedCommit.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 실행
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        // 검증
        Assert.True(transaction.RolledBack);
        Assert.Empty(transaction.Delivered);
    }

    [Fact]
    public async Task Registered_response_freezes_translation_collection()
    {
        // 준비
        var transaction = new ControlledMessageTransaction();
        transaction.MutableTranslations.Add(new(1, "ja", null, "PENDING", null, null));

        // 실행
        var response = await new ChatMessageService(transaction, () => Now).CreateTextAsync(11, 19, "text");
        transaction.MutableTranslations.Clear();

        // 검증
        Assert.Single(response.Translations);
        Assert.Single(Assert.IsType<ChatMessageCreatedIntent>(transaction.Registered[0]).Message.Translations);
    }

    [Fact]
    public async Task Realtime_sender_uses_the_same_creation_flow_once()
    {
        // 준비
        var transaction = new ControlledMessageTransaction();
        IChatRealtimeMessageSender sender = new ChatMessageService(transaction, () => Now);

        // 실행
        await sender.SendTextAsync(11, 19, "text", CancellationToken.None);

        // 검증
        Assert.Equal(1, transaction.ExecuteCount);
        Assert.Equal(1, transaction.InsertCount);
        Assert.Single(transaction.Registered.OfType<ChatMessageCreatedIntent>());
    }
}
