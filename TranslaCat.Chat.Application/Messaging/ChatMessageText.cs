using System.Globalization;

namespace TranslaCat.Chat.Application.Messaging;

public static class ChatMessageText
{
    // Java String.trim은 Unicode 전체 공백이 아니라 U+0020 이하만 제거한다.
    public static string Trim(string value)
    {
        int first = 0;
        int last = value.Length - 1;
        while (first <= last && value[first] <= '\u0020')
        {
            first++;
        }

        while (last >= first && value[last] <= '\u0020')
        {
            last--;
        }

        return value.Substring(first, last - first + 1);
    }

    // 언어/이미지 key의 String.isBlank와 content의 trim 검증은 원본에서 서로 다르다.
    public static bool IsBlank(string? value)
    {
        if (value is null)
        {
            return true;
        }

        return value.All(character => character is (>= '\u0009' and <= '\u000d')
            or (>= '\u001c' and <= '\u001f')
            || (character is not ('\u00a0' or '\u2007' or '\u202f')
                && char.GetUnicodeCategory(character) is UnicodeCategory.SpaceSeparator
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator));
    }
}
