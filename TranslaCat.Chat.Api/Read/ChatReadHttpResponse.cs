using System.Globalization;
using System.Text.Json.Serialization;

namespace TranslaCat.Chat.Api.Read;

public sealed record ChatReadHttpResponse<T>(
    [property: JsonPropertyName("resultCode")] int ResultCode,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("body")] T Body,
    [property: JsonPropertyName("guid")] string Guid,
    [property: JsonPropertyName("createDate")] string CreateDate);

public sealed record ChatReadHttpError(
    [property: JsonPropertyName("errorCode")] string ErrorCode,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("trace")] string Trace);

public sealed class ChatReadHttpResponses(TimeProvider timeProvider, ChatReadLocalTime localTime)
{
    public ChatReadHttpResponse<T> Success<T>(HttpContext context, T body)
    {
        return Create(context, StatusCodes.Status200OK, "OK", body);
    }

    public ChatReadHttpResponse<ChatReadHttpError> Error(
        HttpContext context,
        int statusCode,
        string errorCode,
        string message)
    {
        return Create(context, statusCode, message, new ChatReadHttpError(errorCode, context.Request.Path, ""));
    }

    private ChatReadHttpResponse<T> Create<T>(HttpContext context, int statusCode, string message, T body)
    {
        // envelope 시각은 채팅 UTC timestamp와 달리 원본의 zone-less 패턴을 유지한다.
        var createdAt = localTime.FromUtc(timeProvider.GetUtcNow());

        return new ChatReadHttpResponse<T>(
            statusCode,
            message,
            body,
            context.TraceIdentifier,
            createdAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }
}

// DB의 LocalDateTime을 해석할 원본 시간대는 adapter 연결 전에 명시적으로 확인해야 한다.
public sealed class ChatReadLocalTime(TimeZoneInfo sourceTimeZone)
{
    public TimeZoneInfo SourceTimeZone { get; } = sourceTimeZone;

    public DateTime FromUtc(DateTimeOffset value)
    {
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(value, SourceTimeZone).DateTime, DateTimeKind.Unspecified);
    }

    public DateTime FromUtcForReadPersistence(DateTimeOffset value)
    {
        // 새 읽음 시각만 DATETIME(6) 정밀도로 공급해 최초 응답과 재조회/no-op의 표현을 같게 한다.
        // 기존 cursor와 formatter는 여기서 변경하지 않으며 100ns 잔여값을 반올림하지 않는다.
        var local = FromUtc(value);
        var ticks = local.Ticks - (local.Ticks % TimeSpan.TicksPerMicrosecond);
        return new DateTime(ticks, DateTimeKind.Unspecified);
    }
}
