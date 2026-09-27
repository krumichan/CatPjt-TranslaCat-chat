using System.Net;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Domain;

namespace TranslaCat.Chat.ApiTests;

public sealed class ReadPersistencePrecisionTests
{
    [Theory]
    [InlineData(7_244_381, 7_244_380)]
    [InlineData(9_999_999, 9_999_990)]
    [InlineData(7_244_380, 7_244_380)]
    public void Read_clock_truncates_only_sub_microsecond_ticks_without_rounding(int ticks, int expectedTicks)
    {
        // 준비 — source zone 변환을 유지하면서 저장할 새 읽음 시각만 정규화한다.
        var zone = TimeZoneInfo.CreateCustomTimeZone("ReadPrecision", TimeSpan.FromHours(9), "ReadPrecision", "ReadPrecision");
        var localTime = new ChatReadLocalTime(zone);
        var instant = new DateTimeOffset(2026, 9, 26, 20, 34, 56, TimeSpan.Zero).AddTicks(ticks);

        // 실행
        var persisted = localTime.FromUtcForReadPersistence(instant);
        var unmodified = localTime.FromUtc(instant);

        // 검증 — 날짜 경계와 기존 100ns 변환을 보존하고 DATETIME(6) 값만 절삭한다.
        var localSecond = new DateTime(2026, 9, 27, 5, 34, 56);
        Assert.Equal(localSecond.AddTicks(expectedTicks), persisted);
        Assert.Equal(DateTimeKind.Unspecified, persisted.Kind);
        Assert.Equal(localSecond.AddTicks(ticks), unmodified);
    }

    [Fact]
    public async Task Http_advance_supplies_microsecond_clock_to_policy_and_both_read_events()
    {
        // 준비 — 실제 HTTP DI의 clock 연결을 검사하며 저장소/인증은 이 assembly의 대역이다.
        var clock = new PrecisionTimeProvider();
        await using var fixture = await ReadHttpFixture.StartAsync(timeProvider: clock);

        // 실행
        using var response = await fixture.PatchAsync("{\"lastReadMessageId\":101}");
        var body = (await ReadHttpFixture.ReadJsonAsync(response)).GetProperty("body");

        // 검증 — Domain이 받은 값, HTTP와 자기/방 이벤트가 같은 6자리 읽음 시각이다.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2026-09-26T12:34:56.724438Z", body.GetProperty("lastReadAt").GetString());
        Assert.Equal(new DateTime(2026, 9, 26, 12, 34, 56).AddTicks(7_244_380), fixture.Transaction.CommittedCursor?.LastReadAt);
        Assert.Equal(1, fixture.Transaction.SaveCount);
        Assert.Equal(2, fixture.Transaction.DeliveredPayloads.Count);
        Assert.Equal(body.GetProperty("lastReadAt").GetString(), fixture.Transaction.DeliveredPayloads[0].GetProperty("lastReadAt").GetString());
        Assert.Equal(body.GetProperty("lastReadAt").GetString(), fixture.Transaction.DeliveredPayloads[1].GetProperty("readAt").GetString());
    }

    [Fact]
    public async Task Http_noop_preserves_existing_sub_microsecond_cursor_time_and_formatter_precision()
    {
        // 준비 — 이미 보유한 cursor 값은 새 clock 정규화의 대상이 아니다.
        await using var fixture = await ReadHttpFixture.StartAsync(timeProvider: new PrecisionTimeProvider());
        var previous = new DateTime(2026, 9, 21, 1, 2, 3).AddTicks(7_244_381);
        fixture.Transaction.Member = fixture.Transaction.Member! with
        {
            Cursor = new ReadCursor(101, previous)
        };

        // 실행
        using var response = await fixture.PatchAsync("{\"lastReadMessageId\":101}");
        var body = (await ReadHttpFixture.ReadJsonAsync(response)).GetProperty("body");

        // 검증 — formatter의 9자리 표현을 숨기거나 저장된 시각을 다시 정규화하지 않는다.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, fixture.Transaction.SaveCount);
        Assert.Equal("2026-09-21T01:02:03.724438100Z", body.GetProperty("lastReadAt").GetString());
        Assert.Equal(new ChatReadTimestampFormatter(TimeZoneInfo.Utc).Format(previous), body.GetProperty("lastReadAt").GetString());
        Assert.Equal(body.GetProperty("lastReadAt").GetString(), Assert.Single(fixture.Transaction.DeliveredPayloads).GetProperty("lastReadAt").GetString());
    }

    private sealed class PrecisionTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return new DateTimeOffset(2026, 9, 26, 12, 34, 56, TimeSpan.Zero).AddTicks(7_244_381);
        }
    }
}
