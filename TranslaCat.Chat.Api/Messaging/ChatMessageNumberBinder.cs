using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace TranslaCat.Chat.Api.Messaging;

public sealed class ChatMessageNumberBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext context)
    {
        var supplied = context.ValueProvider.GetValue(context.ModelName);
        if (supplied == ValueProviderResult.None)
        {
            return Task.CompletedTask;
        }
        context.ModelState.SetModelValue(context.ModelName, supplied);
        var text = supplied.FirstValue ?? "";
        var nullable = Nullable.GetUnderlyingType(context.ModelType);
        if (text.Length == 0 && nullable is not null)
        {
            context.Result = ModelBindingResult.Success(null);
            return Task.CompletedTask;
        }

        // Spring StringToNumberConverter/NumberUtils의 내부 공백 제거와 0x/# 정수 입력을 보존한다.
        if (!TryParse(text, out var number)
            || ((nullable ?? context.ModelType) == typeof(int) && (number < int.MinValue || number > int.MaxValue)))
        {
            context.ModelState.TryAddModelError(context.ModelName, "Invalid integer input.");
            context.Result = ModelBindingResult.Failed();
            return Task.CompletedTask;
        }

        object value = (nullable ?? context.ModelType) == typeof(int) ? (object)(int)number : number;
        context.Result = ModelBindingResult.Success(value);
        return Task.CompletedTask;
    }

    private static bool TryParse(string value, out long number)
    {
        number = 0;
        var normalized = new StringBuilder();
        foreach (var character in value)
        {
            var category = char.GetUnicodeCategory(character);
            var whitespace = character is (>= '\u0009' and <= '\u000d') or (>= '\u001c' and <= '\u001f')
                || (category is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                    && character is not ('\u00a0' or '\u2007' or '\u202f'));
            if (whitespace)
            {
                continue;
            }

            // Java parseLong의 BMP decimal digit/fullwidth hex digit 처리도 ASCII로 손실 없이 옮긴다.
            if (category == UnicodeCategory.DecimalDigitNumber)
            {
                normalized.Append((char)('0' + (int)char.GetNumericValue(character)));
            }
            else if (character is (>= '\uff21' and <= '\uff26') or (>= '\uff41' and <= '\uff46'))
            {
                normalized.Append((char)(character - 0xfee0));
            }
            else
            {
                normalized.Append(character);
            }
        }

        var input = normalized.ToString();
        var negative = input.StartsWith('-');
        var unsigned = input.StartsWith('-') || input.StartsWith('+') ? input[1..] : input;
        var hex = unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? unsigned[2..]
            : unsigned.StartsWith('#') ? unsigned[1..] : null;
        if (hex is null)
        {
            return long.TryParse(input, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);
        }
        if (hex.Length == 0 || !BigInteger.TryParse("0" + hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }
        parsed = negative ? -parsed : parsed;
        if (parsed < long.MinValue || parsed > long.MaxValue)
        {
            return false;
        }
        number = (long)parsed;
        return true;
    }
}
