using System.Globalization;

namespace TranslaCat.Chat.Api.Read.Contracts;

public sealed class ChatReadTimestampFormatter
{
    private readonly TimeZoneInfo sourceTimeZone;

    public ChatReadTimestampFormatter(TimeZoneInfo sourceTimeZone)
    {
        ArgumentNullException.ThrowIfNull(sourceTimeZone);
        this.sourceTimeZone = sourceTimeZone;
    }

    public string Format(DateTime value)
    {
        // Java LocalDateTime처럼 zone 없는 값만 받는다. Kind가 있는 값을 다른 zone으로 재해석하지 않는다.
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("읽음 계약 시각은 source time zone의 Unspecified 값이어야 합니다.", nameof(value));
        }

        // Java atZone은 gap만큼 앞으로 이동하고 overlap에서는 이른 instant의 offset을 선택한다.
        var resolvedLocal = value;
        if (sourceTimeZone.IsInvalidTime(resolvedLocal))
        {
            var before = FindAdjacentValidOffset(resolvedLocal, -1);
            var after = FindAdjacentValidOffset(resolvedLocal, 1);
            resolvedLocal = resolvedLocal.Add(after - before);
        }

        var offset = ResolveOffset(resolvedLocal);
        var utc = new DateTimeOffset(resolvedLocal, offset).UtcDateTime;

        // Instant.toString의 0/3/6/9자리 묶음을 보존한다. DateTime은 100ns tick까지만 표현한다.
        var text = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var nanos = utc.Ticks % TimeSpan.TicksPerSecond * 100;
        if (nanos == 0)
        {
            return text + "Z";
        }

        var digits = nanos % 1_000_000 == 0 ? 3 : nanos % 1_000 == 0 ? 6 : 9;
        var fraction = nanos.ToString("D9", CultureInfo.InvariantCulture)[..digits];
        return text + "." + fraction + "Z";
    }

    private TimeSpan ResolveOffset(DateTime local)
    {
        if (sourceTimeZone.IsAmbiguousTime(local))
        {
            // 더 큰 UTC offset이 동일한 벽시계 값에 대해 더 이른 instant를 만든다.
            return sourceTimeZone.GetAmbiguousTimeOffsets(local).Max();
        }

        return sourceTimeZone.GetUtcOffset(local);
    }

    private TimeSpan FindAdjacentValidOffset(DateTime invalidLocal, int direction)
    {
        // 1시간 DST를 가정하지 않고 gap 양쪽의 유효 offset 차이를 찾는다.
        var probe = invalidLocal;
        do
        {
            probe = probe.AddMinutes(direction);
        }
        while (sourceTimeZone.IsInvalidTime(probe));

        return ResolveOffset(probe);
    }
}
