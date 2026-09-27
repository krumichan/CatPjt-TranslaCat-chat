using TranslaCat.Chat.Domain;

namespace TranslaCat.Chat.UnitTests;

public class ReadCursorTests
{
    private static readonly DateTime PreviousReadAt =
        new(2026, 9, 26, 10, 0, 0, DateTimeKind.Unspecified);

    private static readonly DateTime RequestedReadAt =
        new(2026, 9, 26, 11, 0, 0, DateTimeKind.Unspecified);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadCursor_should_advance_from_null_to_first_message(bool hasPreviousReadTime)
    {
        // 준비
        DateTime? previousTime = hasPreviousReadTime ? PreviousReadAt : null;
        var cursor = new ReadCursor(null, previousTime);

        // 실행
        var result = cursor.Advance(42, RequestedReadAt);

        // 검증: 새 상태를 반환하고 기존 값은 변경하지 않는다.
        Assert.True(result.Advanced);
        Assert.Equal(42L, result.Cursor.LastReadMessageId);
        Assert.Equal(RequestedReadAt, result.Cursor.LastReadAt);
        Assert.Null(cursor.LastReadMessageId);
        Assert.Equal(previousTime, cursor.LastReadAt);
    }

    [Theory]
    [InlineData(10L, 11L)]
    [InlineData(10L, 1000L)]
    [InlineData(2147483647L, 2147483648L)]
    [InlineData(9007199254740992L, 9007199254740993L)]
    [InlineData(long.MaxValue - 1, long.MaxValue)]
    public void ReadCursor_should_advance_to_larger_id_without_intermediate_messages(
        long currentId,
        long candidateId)
    {
        // 준비
        var cursor = new ReadCursor(currentId, PreviousReadAt);

        // 실행
        var result = cursor.Advance(candidateId, RequestedReadAt);

        // 검증: int 및 부동소수점 안전 범위를 넘어도 원본 ID를 그대로 보존한다.
        Assert.True(result.Advanced);
        Assert.Equal(candidateId, result.Cursor.LastReadMessageId);
        Assert.Equal(RequestedReadAt, result.Cursor.LastReadAt);
    }

    [Theory]
    [InlineData(42L, 42L)]
    [InlineData(42L, 41L)]
    [InlineData(long.MaxValue, long.MaxValue)]
    [InlineData(long.MaxValue, long.MaxValue - 1)]
    [InlineData(long.MaxValue, long.MinValue)]
    public void ReadCursor_should_preserve_state_for_equal_or_older_id(
        long currentId,
        long candidateId)
    {
        // 준비
        var cursor = new ReadCursor(currentId, PreviousReadAt);

        // 실행: 더 늦은 시각으로 요청해도 ID가 전진하지 않으면 no-op이다.
        var result = cursor.Advance(candidateId, RequestedReadAt);

        // 검증
        Assert.False(result.Advanced);
        Assert.Equal(cursor, result.Cursor);
        Assert.Equal(PreviousReadAt, result.Cursor.LastReadAt);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(42L, false)]
    [InlineData(42L, true)]
    public void ReadCursor_should_preserve_nullable_state_for_null_candidate(
        long? currentId,
        bool hasPreviousReadTime)
    {
        // 준비
        DateTime? previousTime = hasPreviousReadTime ? PreviousReadAt : null;
        var cursor = new ReadCursor(currentId, previousTime);

        // 실행
        var result = cursor.Advance(null, RequestedReadAt);

        // 검증
        Assert.False(result.Advanced);
        Assert.Equal(cursor, result.Cursor);
        Assert.Equal(previousTime, result.Cursor.LastReadAt);
    }

    [Theory]
    [InlineData(42L)]
    [InlineData(41L)]
    public void ReadCursor_should_preserve_null_read_time_on_noop(long candidateId)
    {
        // 준비
        var cursor = new ReadCursor(42, null);

        // 실행
        var result = cursor.Advance(candidateId, RequestedReadAt);

        // 검증
        Assert.False(result.Advanced);
        Assert.Equal(42L, result.Cursor.LastReadMessageId);
        Assert.Null(result.Cursor.LastReadAt);
    }

    [Fact]
    public void ReadCursor_should_replace_null_read_time_when_advancing()
    {
        // 준비
        var cursor = new ReadCursor(42, null);

        // 실행
        var result = cursor.Advance(43, RequestedReadAt);

        // 검증
        Assert.True(result.Advanced);
        Assert.Equal(43L, result.Cursor.LastReadMessageId);
        Assert.Equal(RequestedReadAt, result.Cursor.LastReadAt);
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void ReadCursor_should_apply_supplied_time_even_when_earlier(DateTimeKind kind)
    {
        // 준비: 시계 보정이나 시간대 변환 없이 전달값 자체를 사용하는지 확인한다.
        var cursor = new ReadCursor(42, PreviousReadAt);
        var suppliedTime = new DateTime(2026, 9, 25, 9, 0, 0, kind).AddTicks(1234);

        // 실행
        var result = cursor.Advance(43, suppliedTime);

        // 검증: DateTime 동등성만으로는 Kind 보존을 확인할 수 없어 함께 검사한다.
        Assert.True(result.Advanced);
        Assert.Equal(43L, result.Cursor.LastReadMessageId);
        Assert.Equal(suppliedTime, result.Cursor.LastReadAt);
        Assert.Equal(kind, result.Cursor.LastReadAt?.Kind);
    }

    [Theory]
    [InlineData(null, 0L)]
    [InlineData(null, long.MinValue)]
    [InlineData(long.MinValue, 0L)]
    public void ReadCursor_should_not_add_http_positive_id_validation(
        long? currentId,
        long candidateId)
    {
        // 준비: 원본 entity는 양수 검증 없이 signed Long의 순서만 비교한다.
        var cursor = new ReadCursor(currentId, PreviousReadAt);

        // 실행
        var result = cursor.Advance(candidateId, RequestedReadAt);

        // 검증: HTTP 요청 허용 여부를 검증하는 테스트는 아니다.
        Assert.True(result.Advanced);
        Assert.Equal(candidateId, result.Cursor.LastReadMessageId);
        Assert.Equal(RequestedReadAt, result.Cursor.LastReadAt);
    }

    [Fact]
    public void ReadCursor_should_preserve_cursor_and_time_at_each_sequential_step()
    {
        // 준비: 중복, 역순, null, 건너뛰기를 섞은 순차 요청이며 DB 경합은 다루지 않는다.
        var cursor = new ReadCursor(10, PreviousReadAt);
        var firstAdvanceAt = PreviousReadAt.AddMinutes(1);
        var secondAdvanceAt = PreviousReadAt.AddMinutes(5);
        var thirdAdvanceAt = PreviousReadAt.AddMinutes(8);
        (long? Candidate, long ExpectedId, bool Advanced, DateTime ExpectedTime)[] steps =
        [
            (20, 20, true, firstAdvanceAt),
            (20, 20, false, firstAdvanceAt),
            (15, 20, false, firstAdvanceAt),
            (null, 20, false, firstAdvanceAt),
            (40, 40, true, secondAdvanceAt),
            (30, 40, false, secondAdvanceAt),
            (25, 40, false, secondAdvanceAt),
            (41, 41, true, thirdAdvanceAt),
            (10, 41, false, thirdAdvanceAt),
            (41, 41, false, thirdAdvanceAt)
        ];

        for (var index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            var requestedTime = PreviousReadAt.AddMinutes(index + 1);

            // 실행: 앞선 결과를 다음 요청의 현재 상태로 사용한다.
            var result = cursor.Advance(step.Candidate, requestedTime);

            // 검증: 최종 최대값뿐 아니라 매 단계 전진 여부와 시각 보존을 확인한다.
            Assert.Equal(step.Advanced, result.Advanced);
            Assert.Equal(step.ExpectedId, result.Cursor.LastReadMessageId);
            Assert.Equal(step.ExpectedTime, result.Cursor.LastReadAt);
            Assert.True(result.Cursor.LastReadMessageId >= cursor.LastReadMessageId);

            cursor = result.Cursor;
        }
    }

    [Theory]
    [InlineData(10L, 20L, 30L, 3)]
    [InlineData(10L, 30L, 20L, 2)]
    [InlineData(20L, 10L, 30L, 3)]
    [InlineData(20L, 30L, 10L, 2)]
    [InlineData(30L, 10L, 20L, 1)]
    [InlineData(30L, 20L, 10L, 1)]
    public void ReadCursor_should_finish_at_maximum_for_every_order_of_same_ids(
        long firstId,
        long secondId,
        long thirdId,
        int maximumArrivalMinute)
    {
        // 준비: 같은 ID 집합의 모든 순열에 각 요청 순서별 고정 시각을 부여한다.
        var cursor = new ReadCursor(null, null);
        long[] candidates = [firstId, secondId, thirdId];

        // 실행
        for (var index = 0; index < candidates.Length; index++)
        {
            var result = cursor.Advance(candidates[index], PreviousReadAt.AddMinutes(index + 1));
            cursor = result.Cursor;
        }

        // 검증: 최종 cursor는 순서와 무관하지만 시각은 최대 ID가 도착한 순서를 따른다.
        Assert.Equal(30L, cursor.LastReadMessageId);
        Assert.Equal(PreviousReadAt.AddMinutes(maximumArrivalMinute), cursor.LastReadAt);
    }
}
