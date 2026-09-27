using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.UnitTests.Messaging;

public sealed class ChatMessagePaginationTests
{
    private static ChatMessageService Service(ControlledMessageTransaction transaction)
    {
        return new(transaction, () => throw new InvalidOperationException("조회는 시계를 사용하지 않아야 합니다."));
    }

    [Fact]
    public async Task Backward_page_trims_sentinel_then_reverses_and_returns_oldest_cursor()
    {
        // 준비
        var transaction = new ControlledMessageTransaction();
        transaction.FetchResults.Enqueue(Enumerable.Range(1, 101).Reverse()
            .Select(id => ControlledMessageTransaction.Message(id)).ToArray());

        // 실행
        var page = await Service(transaction).GetMessagesAsync(11, 19, 200);

        // 검증
        Assert.Equal(Enumerable.Range(2, 100).Select(id => (long)id), page.Messages.Select(message => message.Id));
        Assert.Equal(2, page.NextCursorId);
        Assert.True(page.HasNext);
        Assert.Empty(transaction.FindCalls);
        Assert.Equal([(200L, false, 101)], transaction.FetchCalls);
        Assert.Equal([(11L, 19L, false)], transaction.AccessCalls);
        Assert.Empty(transaction.Registered);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task Backward_final_page_has_no_cursor(int count)
    {
        // 준비
        var transaction = new ControlledMessageTransaction();
        transaction.FetchResults.Enqueue(Enumerable.Range(1, count).Reverse()
            .Select(id => ControlledMessageTransaction.Message(id)).ToArray());

        // 실행
        var page = await Service(transaction).GetMessagesAsync(11, 19);

        // 검증
        Assert.Equal(count, page.Messages.Count);
        Assert.False(page.HasNext);
        Assert.Null(page.NextCursorId);
    }

    [Fact]
    public async Task Forward_page_validates_cursor_and_uses_last_returned_id()
    {
        // 준비
        var transaction = new ControlledMessageTransaction { Accessible = ControlledMessageTransaction.Message(9007199254740993) };
        transaction.FetchResults.Enqueue([ControlledMessageTransaction.Message(9007199254740994),
            ControlledMessageTransaction.Message(9007199254740995), ControlledMessageTransaction.Message(9007199254740996)]);

        // 실행
        var page = await Service(transaction).GetMessagesAfterAsync(11, 19, 9007199254740993, 2);

        // 검증
        Assert.Equal([9007199254740993L], transaction.FindCalls);
        Assert.Equal([(9007199254740993L, true, 3)], transaction.FetchCalls);
        Assert.Equal([9007199254740994L, 9007199254740995L], page.Messages.Select(message => message.Id));
        Assert.Equal(9007199254740995L, page.NextCursorId);
        Assert.True(page.HasNext);
    }

    [Fact]
    public async Task Anchor_with_zero_sides_retains_anchor_cursor_for_nonempty_directions()
    {
        // 준비
        var transaction = new ControlledMessageTransaction { Accessible = ControlledMessageTransaction.Message(20) };
        transaction.FetchResults.Enqueue([ControlledMessageTransaction.Message(19)]);
        transaction.FetchResults.Enqueue([ControlledMessageTransaction.Message(21)]);

        // 실행
        var page = await Service(transaction).GetMessagesAroundAnchorAsync(11, 19, 20, 0, 0);

        // 검증
        Assert.Equal([20L], page.Messages.Select(message => message.Id));
        Assert.Equal(20, page.PreviousCursorId);
        Assert.Equal(20, page.NextCursorId);
        Assert.True(page.HasPrevious);
        Assert.True(page.HasNext);
        Assert.Equal([(20L, false, 1), (20L, true, 1)], transaction.FetchCalls);
    }

    [Fact]
    public async Task Anchor_window_returns_ascending_nearest_messages_only()
    {
        // 준비
        var transaction = new ControlledMessageTransaction { Accessible = ControlledMessageTransaction.Message(20) };
        transaction.FetchResults.Enqueue([ControlledMessageTransaction.Message(19), ControlledMessageTransaction.Message(18)]);
        transaction.FetchResults.Enqueue([ControlledMessageTransaction.Message(21), ControlledMessageTransaction.Message(22)]);

        // 실행
        var page = await Service(transaction).GetMessagesAroundAnchorAsync(11, 19, 20, 1, 1);

        // 검증
        Assert.Equal([19L, 20L, 21L], page.Messages.Select(message => message.Id));
        Assert.Equal(19, page.PreviousCursorId);
        Assert.Equal(21, page.NextCursorId);
        Assert.Equal([19L, 20L, 21L], transaction.Presented.Select(message => message.Id));
    }

    [Theory]
    [InlineData(false, "CHAT_MESSAGE_FORWARD_CURSOR_NOT_ACCESSIBLE")]
    [InlineData(true, "CHAT_MESSAGE_ANCHOR_NOT_ACCESSIBLE")]
    public async Task Inaccessible_cursor_fails_after_membership_and_before_window_query(bool anchor, string expectedCode)
    {
        // 준비
        var transaction = new ControlledMessageTransaction();

        // 실행
        var error = await Assert.ThrowsAsync<ChatMessageException>(() => anchor
            ? Service(transaction).GetMessagesAroundAnchorAsync(11, 19, 20)
            : Service(transaction).GetMessagesAfterAsync(11, 19, 20));

        // 검증
        Assert.Equal(expectedCode, error.ErrorCode);
        Assert.Single(transaction.AccessCalls);
        Assert.Empty(transaction.FetchCalls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task Backward_invalid_cursor_never_starts_transaction(long cursor)
    {
        // 준비
        var transaction = new ControlledMessageTransaction();

        // 실행
        await Assert.ThrowsAsync<ChatMessageException>(() => Service(transaction).GetMessagesAsync(11, 19, cursor));

        // 검증
        Assert.Equal(0, transaction.ExecuteCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Forward_invalid_size_has_original_error_code(int size)
    {
        // 준비
        var transaction = new ControlledMessageTransaction();

        // 실행
        var error = await Assert.ThrowsAsync<ChatMessageException>(() => Service(transaction).GetMessagesAfterAsync(11, 19, 1, size));

        // 검증
        Assert.Equal("CHAT_MESSAGE_FORWARD_SIZE_INVALID", error.ErrorCode);
        Assert.Equal(0, transaction.ExecuteCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task Anchor_invalid_side_has_original_error_code(int size)
    {
        // 준비
        var transaction = new ControlledMessageTransaction();

        // 실행
        var error = await Assert.ThrowsAsync<ChatMessageException>(() => Service(transaction).GetMessagesAroundAnchorAsync(11, 19, 1, size));

        // 검증
        Assert.Equal("CHAT_MESSAGE_ANCHOR_SIZE_INVALID", error.ErrorCode);
        Assert.Equal(0, transaction.ExecuteCount);
    }
}
