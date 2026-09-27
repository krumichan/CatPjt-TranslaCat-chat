using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TranslaCat.Chat.Api.Read.Contracts;

public sealed class ChatReadMessageIdConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // 원본 Jackson Long의 문자열 coercion을 보존한다. 양수 검증은 역직렬화 뒤 DTO에서 한다.
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = TrimJavaWhitespace(reader.GetString()!);
            if (value.Length == 0 || value == "null")
            {
                return null;
            }

            var decimalDigits = NormalizeJavaDecimalDigits(value);
            if (long.TryParse(decimalDigits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
            {
                return integer;
            }

            throw new JsonException("정수 ID 문자열의 형식 또는 범위가 올바르지 않습니다.");
        }

        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("메시지 ID는 Long으로 변환할 수 있어야 합니다.");
        }

        // integer token은 double을 거치지 않아 원본 64-bit ID를 정확하게 보존한다.
        using var document = JsonDocument.ParseValue(ref reader);
        var number = document.RootElement;
        var token = number.GetRawText();
        if (!token.Contains('.') && !token.Contains('e') && !token.Contains('E'))
        {
            if (number.TryGetInt64(out var integer))
            {
                return integer;
            }

            throw new JsonException("메시지 ID가 Long 범위를 벗어났습니다.");
        }

        // Jackson 기본 float coercion은 double 후 0 방향 절삭이다. Java cast의 경계 포화도 구분한다.
        var floating = number.GetDouble();
        const double upperBoundary = 9223372036854775808d;
        if (!double.IsFinite(floating) || floating < long.MinValue || floating > upperBoundary)
        {
            throw new JsonException("메시지 ID가 Long 범위를 벗어났습니다.");
        }

        return floating == upperBoundary ? long.MaxValue : (long)floating;
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is long messageId)
        {
            writer.WriteNumberValue(messageId);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static string TrimJavaWhitespace(string value)
    {
        var first = 0;
        var last = value.Length;

        while (first < last && value[first] <= ' ')
        {
            first++;
        }

        while (last > first && value[last - 1] <= ' ')
        {
            last--;
        }

        return value[first..last];
    }

    private static string NormalizeJavaDecimalDigits(string value)
    {
        // JDK Long.parseLong은 BMP의 decimal digit도 허용한다. 부호나 다른 숫자 문자는 바꾸지 않는다.
        var digits = value.ToCharArray();
        for (var index = 0; index < digits.Length; index++)
        {
            if (char.GetUnicodeCategory(digits[index]) == UnicodeCategory.DecimalDigitNumber)
            {
                digits[index] = (char)('0' + (int)char.GetNumericValue(digits[index]));
            }
        }

        return new string(digits);
    }
}
