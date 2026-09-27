using TranslaCat.Chat.Application.Ai;

namespace TranslaCat.Chat.UnitTests.Ai;

public sealed class ChatAiPolicyTests
{
    [Theory]
    [InlineData("@Mika 안녕!", "Mika", true)]
    [InlineData("@Mi도 같이", "Mi", true)]
    [InlineData("@Mikaela는 다른 이름", "Mika", false)]
    [InlineData("@mIkA hello", "Mika", true)]
    [InlineData("user@Mika.com", "Mika", false)]
    [InlineData("_@Mika", "Mika", false)]
    [InlineData("@Mika는", "Mika", true)]
    [InlineData("@Mika이랑 같이", "Mika", true)]
    [InlineData("@Mika나다", "Mika", false)]
    [InlineData("@Mika\u00a0", "Mika", false)]
    [InlineData("😀@Mika😀", "Mika", true)]
    [InlineData("𐐀@Mika", "Mika", false)]
    [InlineData("@M.i+!", "M.i+", true)]
    [InlineData(" ", "Mika", false)]
    public void Mention_boundaries_preserve_explicit_names_and_known_korean_particles(string content, string nickname, bool expected)
    {
        // 준비 / 실행
        bool result = ChatAiPolicies.ContainsMention(content, nickname);

        // 검증
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1L, 3, 1)]
    [InlineData(4294967296L, 3, 1)]
    [InlineData(long.MaxValue, 3, 1)]
    public void Conversation_selection_uses_Java_long_hash_and_positive_floor_mod(long id, int count, int expected)
    {
        // 준비 / 실행
        int result = ChatAiPolicies.ConversationCandidateIndex(id, count);

        // 검증
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("😀", 1, 2104)]
    [InlineData("가", 0, 1200)]
    [InlineData("가", -1, 1200)]
    [InlineData("가", 2, 2104)]
    public void Humanized_delay_counts_codepoints_and_clamps_random_ratio(string text, double ratio, int expectedMillis)
    {
        // 준비 / 실행
        var result = ChatAiPolicies.ResponseDelay(text, 1200, 3500, ratio);

        // 검증
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMillis), result);
    }

    [Fact]
    public void Long_response_delay_remains_bounded_and_java_rounding_is_not_bankers_rounding()
    {
        // 준비
        var longText = new string('가', 800);

        // 실행
        var maximum = ChatAiPolicies.ResponseDelay(longText, 1200, 3500, 1);
        var midpoint = ChatAiPolicies.ResponseDelay("", 1200, 1201, 0.5);

        // 검증
        Assert.Equal(TimeSpan.FromMilliseconds(3500), maximum);
        Assert.Equal(TimeSpan.FromMilliseconds(1201), midpoint);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(21, true)]
    [InlineData(22, false)]
    public void Revival_window_includes_start_and_excludes_end(int hour, bool expected)
    {
        // 준비
        var now = new DateTime(2026, 8, 9, hour, 0, 0);

        // 실행
        bool result = ChatAiPolicies.WithinRevivalWindow(now, TimeSpan.FromHours(10), TimeSpan.FromHours(22));

        // 검증
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Revival_distribution_preserves_minimum_delay_and_next_allowed_day()
    {
        // 준비
        var human = new DateTime(2026, 8, 9, 23, 0, 0);
        var earliest = human.AddHours(24);

        // 실행
        var first = ChatAiPolicies.ScheduleRevival(10, 1, 1, earliest, TimeSpan.FromHours(10), TimeSpan.FromHours(22));
        var repeated = ChatAiPolicies.ScheduleRevival(10, 1, 1, earliest, TimeSpan.FromHours(10), TimeSpan.FromHours(22));

        // 검증
        Assert.Equal(human.Date.AddDays(2), first.Date);
        Assert.Equal(first, repeated);
        Assert.True(first >= earliest);
        Assert.InRange(first.TimeOfDay, TimeSpan.FromHours(10), TimeSpan.FromHours(22) - TimeSpan.FromTicks(1));
    }

    [Fact]
    public void Revival_retry_does_not_distribute_and_moves_to_next_window_start()
    {
        // 준비
        var failed = new DateTime(2026, 8, 9, 21, 59, 0);

        // 실행
        var retry = ChatAiPolicies.ScheduleRevivalRetry(failed, 5, TimeSpan.FromHours(10), TimeSpan.FromHours(22));

        // 검증
        Assert.Equal(new DateTime(2026, 8, 10, 10, 0, 0), retry);
    }
}
