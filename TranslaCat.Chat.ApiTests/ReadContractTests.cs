using System.Text.Json;
using System.Text.Json.Serialization;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Application.Read;

namespace TranslaCat.Chat.ApiTests;

public sealed class ReadContractTests
{
    [Theory]
    [InlineData(0L, "2026-09-26T12:34:56Z")]
    [InlineData(1230000L, "2026-09-26T12:34:56.123Z")]
    [InlineData(1234560L, "2026-09-26T12:34:56.123456Z")]
    [InlineData(1234567L, "2026-09-26T12:34:56.123456700Z")]
    [InlineData(1L, "2026-09-26T12:34:56.000000100Z")]
    public void Timestamp_retains_tick_precision_using_Java_instant_fraction_groups(long ticks, string expected)
    {
        // 준비
        var formatter = new ChatReadTimestampFormatter(TimeZoneInfo.Utc);
        var value = new DateTime(2026, 9, 26, 12, 34, 56).AddTicks(ticks);

        // 실행
        var formatted = formatter.Format(value);

        // 검증: Java의 나노초 전 범위가 아니라 DateTime으로 표현 가능한 합성 tick 사례다.
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void Source_local_time_is_converted_using_explicit_zone_instead_of_appending_Z()
    {
        // 준비
        var zone = TimeZoneInfo.CreateCustomTimeZone("SyntheticPlusNine", TimeSpan.FromHours(9), "Synthetic +09", "Synthetic +09");
        var formatter = new ChatReadTimestampFormatter(zone);

        // 실행
        var formatted = formatter.Format(new DateTime(2026, 9, 26, 1, 2, 3));

        // 검증
        Assert.Equal("2026-09-25T16:02:03Z", formatted);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    public void Kind_bearing_value_is_rejected_instead_of_silently_reinterpreted(DateTimeKind kind)
    {
        // 준비
        var formatter = new ChatReadTimestampFormatter(TimeZoneInfo.Utc);
        var value = new DateTime(2026, 9, 26, 12, 34, 56, kind);

        // 실행 / 검증
        Assert.Throws<ArgumentException>(() => formatter.Format(value));
    }

    [Fact]
    public void Dst_gap_moves_forward_by_actual_offset_change()
    {
        // 준비: 30분 gap을 사용하여 1시간 상수를 가정하지 않는지 확인한다.
        var zone = CreateSyntheticDstZone();
        var value = new DateTime(2026, 3, 15, 2, 10, 0);
        Assert.True(zone.IsInvalidTime(value));
        var formatter = new ChatReadTimestampFormatter(zone);

        // 실행
        var formatted = formatter.Format(value);

        // 검증: 02:10 -> 02:40 (+00:30) -> 02:10Z.
        Assert.Equal("2026-03-15T02:10:00Z", formatted);
    }

    [Fact]
    public void Dst_overlap_uses_earlier_instant_offset()
    {
        // 준비
        var zone = CreateSyntheticDstZone();
        var value = new DateTime(2026, 10, 15, 1, 45, 0);
        Assert.True(zone.IsAmbiguousTime(value));
        var formatter = new ChatReadTimestampFormatter(zone);

        // 실행
        var formatted = formatter.Format(value);

        // 검증: 중복되는 01:45 중 더 큰 +00:30 offset을 사용한다.
        Assert.Equal("2026-10-15T01:15:00Z", formatted);
    }

    [Fact]
    public void Response_preserves_explicit_null_fields_and_large_numeric_ids()
    {
        // 준비
        var mapper = new ChatReadContractMapper(TimeZoneInfo.Utc);
        var response = new ChatRoomReadResponse(long.MaxValue, 9007199254740993L, null, long.MaxValue);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        // 실행
        var json = JsonSerializer.SerializeToElement(mapper.ToResponse(response), options);

        // 검증
        ReadHttpPipelineTests.AssertFields(json, "chatRoomId", "lastReadMessageId", "lastReadAt", "unreadCount");
        Assert.Equal("9223372036854775807", json.GetProperty("chatRoomId").GetRawText());
        Assert.Equal("9007199254740993", json.GetProperty("lastReadMessageId").GetRawText());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("lastReadAt").ValueKind);
        Assert.Equal(long.MaxValue, json.GetProperty("unreadCount").GetInt64());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Self_event_keeps_flat_wire_fields_and_exposes_FE_nullable_mismatch(bool nullReadTime)
    {
        // 준비: 실제 FE를 실행하지 않고 parser의 string 필수 조건을 합성 payload에 적용한다.
        var mapper = new ChatReadContractMapper(TimeZoneInfo.Utc);
        var readAt = nullReadTime ? (DateTime?)null : new DateTime(2026, 9, 21, 1, 2, 3);
        var intent = new ChatReadUpdated("synthetic@example.invalid", 73, new ChatRoomReadResponse(41, 100, readAt, 13));

        // 실행
        var json = JsonSerializer.SerializeToElement(mapper.ToSelfEvent(intent, new DateTime(2026, 9, 26, 12, 0, 0)));
        var acceptedByObservedFeShape = MatchesObservedSelfParserShape(json);

        // 검증: null은 제거하거나 현재 시각으로 대체하지 않는다.
        ReadHttpPipelineTests.AssertFields(json, "eventType", "chatRoomId", "userId", "lastReadMessageId", "lastReadAt", "unreadCount", "occurredAt");
        Assert.Equal("chat.read.updated", json.GetProperty("eventType").GetString());
        Assert.Equal(nullReadTime ? null : "2026-09-21T01:02:03Z", json.GetProperty("lastReadAt").GetString());
        Assert.Equal("2026-09-26T12:00:00Z", json.GetProperty("occurredAt").GetString());
        Assert.Equal(!nullReadTime, acceptedByObservedFeShape);
        Assert.DoesNotContain("synthetic@example.invalid", json.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Member_event_preserves_selected_identity_previous_new_and_occurred_time(bool openRoom)
    {
        // 준비
        var mapper = new ChatReadContractMapper(TimeZoneInfo.Utc);
        var intent = new ChatMemberReadUpdated(
            41, openRoom ? null : 73, openRoom ? 91 : null,
            9007199254740992L, 9007199254740993L, new DateTime(2026, 9, 21, 1, 2, 3));

        // 실행
        var json = JsonSerializer.SerializeToElement(mapper.ToMemberEvent(intent, new DateTime(2026, 9, 26, 12, 0, 0)));

        // 검증
        ReadHttpPipelineTests.AssertFields(json, "eventType", "chatRoomId", "readerUserId", "readerOpenChatMemberId", "previousLastReadMessageId", "lastReadMessageId", "readAt", "occurredAt");
        Assert.Equal("chat.member.read.updated", json.GetProperty("eventType").GetString());
        Assert.Equal(openRoom ? JsonValueKind.Null : JsonValueKind.Number, json.GetProperty("readerUserId").ValueKind);
        Assert.Equal(openRoom ? JsonValueKind.Number : JsonValueKind.Null, json.GetProperty("readerOpenChatMemberId").ValueKind);
        Assert.Equal("9007199254740992", json.GetProperty("previousLastReadMessageId").GetRawText());
        Assert.Equal("9007199254740993", json.GetProperty("lastReadMessageId").GetRawText());
        Assert.Equal("2026-09-21T01:02:03Z", json.GetProperty("readAt").GetString());
        Assert.Equal("2026-09-26T12:00:00Z", json.GetProperty("occurredAt").GetString());
    }

    [Fact]
    public void Nullable_member_payload_fields_remain_present_under_ignore_null_settings()
    {
        // 준비
        var mapper = new ChatReadContractMapper(TimeZoneInfo.Utc);
        var intent = new ChatMemberReadUpdated(41, null, 91, null, null, null);
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        // 실행
        var json = JsonSerializer.SerializeToElement(mapper.ToMemberEvent(intent, new DateTime(2026, 9, 26)), options);

        // 검증: mapper 검증이며 누락 cursor가 실제 publisher의 전송 조건을 통과한다는 뜻은 아니다.
        foreach (var name in new[] { "readerUserId", "previousLastReadMessageId", "lastReadMessageId", "readAt" })
        {
            Assert.Equal(JsonValueKind.Null, json.GetProperty(name).ValueKind);
        }
    }

    private static bool MatchesObservedSelfParserShape(JsonElement json)
    {
        return json.GetProperty("eventType").GetString() == "chat.read.updated"
                && json.GetProperty("chatRoomId").ValueKind == JsonValueKind.Number
                && json.GetProperty("userId").ValueKind == JsonValueKind.Number
                && json.GetProperty("lastReadMessageId").ValueKind == JsonValueKind.Number
                && json.GetProperty("lastReadAt").ValueKind == JsonValueKind.String
                && json.GetProperty("unreadCount").ValueKind == JsonValueKind.Number
                && json.GetProperty("occurredAt").ValueKind == JsonValueKind.String;
    }

    private static TimeZoneInfo CreateSyntheticDstZone()
    {
        var changeTime = new DateTime(1, 1, 1, 2, 0, 0);
        var start = TimeZoneInfo.TransitionTime.CreateFixedDateRule(changeTime, 3, 15);
        var end = TimeZoneInfo.TransitionTime.CreateFixedDateRule(changeTime, 10, 15);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromMinutes(30), start, end);

        return TimeZoneInfo.CreateCustomTimeZone(
            "SyntheticHalfHourDst", TimeSpan.Zero, "Synthetic half-hour DST", "Synthetic standard", "Synthetic daylight", [rule]);
    }
}
