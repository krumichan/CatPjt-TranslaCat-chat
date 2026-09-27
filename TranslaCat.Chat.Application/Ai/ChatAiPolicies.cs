using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Application.Ai;

public static class ChatAiPolicies
{
    private static readonly string[] KoreanParticles = ["은", "는", "이", "가", "을", "를", "도", "만", "와", "과", "랑", "이랑", "의", "에", "에서", "에게", "한테", "께", "께서", "으로", "로", "부터", "까지", "처럼", "보다", "하고"];

    public static bool ContainsMention(string? content, string? nickname)
    {
        if (content is null || ChatMessageText.IsBlank(content)
            || nickname is null || ChatMessageText.IsBlank(nickname))
        {
            return false;
        }

        // 부분 닉네임과 이메일을 제외하면서 원본이 열거한 한국어 조사만 추가 경계로 인정한다.
        string expression = "@" + Regex.Escape(ChatMessageText.Trim(nickname));
        foreach (Match match in Regex.Matches(content, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)))
        {
            if (match.Index > 0)
            {
                Rune.DecodeLastFromUtf16(content.AsSpan(0, match.Index), out var previous, out _);
                if (Rune.IsLetterOrDigit(previous) || previous.Value == '_' || Rune.GetUnicodeCategory(previous)
                    is UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber)
                {
                    continue;
                }
            }

            int end = match.Index + match.Length;
            if (MentionBoundary(content, end) || KoreanParticles.Any(particle =>
                content.AsSpan(end).StartsWith(particle, StringComparison.Ordinal)
                && MentionBoundary(content, end + particle.Length)))
            {
                return true;
            }
        }

        return false;
    }

    public static int ConversationCandidateIndex(long messageId, int candidateCount)
    {
        if (candidateCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(candidateCount));
        }

        int hash = unchecked((int)(messageId ^ (long)((ulong)messageId >> 32)));
        return (int)FloorMod(hash, candidateCount);
    }

    public static TimeSpan ResponseDelay(string? reply, int minimumMillis, int maximumMillis, double randomRatio)
    {
        // Java codePointCount와 Math.round의 양수 규칙을 보존하며 임의 모델/발화 빈도는 바꾸지 않는다.
        long minimum = minimumMillis;
        if (maximumMillis <= minimum)
        {
            return TimeSpan.FromMilliseconds(minimum);
        }

        long count = reply?.EnumerateRunes().Count() ?? 0;
        long upper = Math.Min(maximumMillis, minimum + 900 + (count * 4));
        double boundedRatio = Math.Clamp(randomRatio, 0, 1);
        return TimeSpan.FromMilliseconds(minimum + (long)Math.Floor(((upper - minimum) * boundedRatio) + 0.5));
    }

    public static bool WithinRevivalWindow(DateTime value, TimeSpan start, TimeSpan end)
    {
        ValidateWindow(start, end);
        return value.TimeOfDay >= start && value.TimeOfDay < end;
    }

    public static DateTime ScheduleRevival(long roomId, long cycleVersion, int stage,
        DateTime earliest, TimeSpan start, TimeSpan end)
    {
        ValidateWindow(start, end);
        var date = earliest.Date;
        var windowStart = date + start;
        var windowEnd = date + end;
        if (earliest >= windowEnd)
        {
            date = date.AddDays(1);
            windowStart = date + start;
            windowEnd = date + end;
        }
        else if (earliest > windowStart)
        {
            windowStart = earliest;
        }

        long availableSeconds = (windowEnd - windowStart).Ticks / TimeSpan.TicksPerSecond;
        if (availableSeconds <= 1)
        {
            return windowStart;
        }

        // Java Objects.hash(Long, Long, Integer, Long)와 signed floorMod 분산을 그대로 계산한다.
        long epochDay = (date - new DateTime(1970, 1, 1)).Days;
        int seed = 1;
        seed = unchecked((31 * seed) + LongHash(roomId));
        seed = unchecked((31 * seed) + LongHash(cycleVersion));
        seed = unchecked((31 * seed) + stage);
        seed = unchecked((31 * seed) + LongHash(epochDay));
        ulong mixed = unchecked((ulong)(long)seed + 0x9E3779B97F4A7C15UL);
        mixed = unchecked((mixed ^ (mixed >> 30)) * 0xBF58476D1CE4E5B9UL);
        mixed = unchecked((mixed ^ (mixed >> 27)) * 0x94D049BB133111EBUL);
        long result = unchecked((long)(mixed ^ (mixed >> 31)));
        return windowStart.AddSeconds(FloorMod(result, availableSeconds));
    }

    public static DateTime ScheduleRevivalRetry(DateTime failedAt, int delayMinutes, TimeSpan start, TimeSpan end)
    {
        ValidateWindow(start, end);
        var earliest = failedAt.AddMinutes(Math.Max(1, delayMinutes));
        if (earliest.TimeOfDay < start)
        {
            return earliest.Date + start;
        }

        return earliest.TimeOfDay >= end ? earliest.Date.AddDays(1) + start : earliest;
    }

    private static int LongHash(long value)
    {
        return unchecked((int)(value ^ (long)((ulong)value >> 32)));
    }

    private static long FloorMod(long value, long divisor)
    {
        return ((value % divisor) + divisor) % divisor;
    }

    private static void ValidateWindow(TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero || end >= TimeSpan.FromDays(1) || start >= end)
        {
            throw new ArgumentException("REVIVAL 허용 시작 시간은 종료 시간보다 빨라야 합니다.");
        }
    }

    private static bool MentionBoundary(string value, int index)
    {
        if (index == value.Length)
        {
            return true;
        }

        // Java (?iu)의 \s는 ASCII 공백이다. Unicode separator를 임의 멘션 경계로 넓히지 않는다.
        Rune.TryGetRuneAt(value, index, out var rune);
        return rune.Value is ' ' or '\t' or '\n' or '\v' or '\f' or '\r'
            || Rune.GetUnicodeCategory(rune) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol
                or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;
    }
}
