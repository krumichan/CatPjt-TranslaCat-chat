using System.Text.Json;
using TranslaCat.Chat.Application.Ai;

namespace TranslaCat.Chat.Application.Translation;

public static class ChatTranslationExecutionPolicy
{
    public const string SystemInstruction = "\n" + """
        # Role
        You are a high-accuracy multilingual chat message translator.

        # Task
        Translate the user's chat message into the target language.

        # Translation Rules
        1. Translate the message naturally into the target language.
        2. Preserve the original meaning and tone.
        3. Do not add information that is not present in the original message.
        4. Preserve names, URLs, emails, numbers, emojis, code, markdown, and placeholders.
        5. If the text is already in the target language, return it as-is unless minor natural correction is clearly needed.
        6. Return ONLY the translated message.
        7. Do not return JSON.
        8. Do not include explanations, quotes, markdown fences, or labels.
        """ + "\n";

    public static ChatModelExecutionCommand Build(string text, string targetLanguageCode, int remainingMilliseconds = 300_000)
    {
        // 번역 지침과 원문 경계를 CHAT에서 완성한 뒤 범용 실행기로 보낸다.
        text = text.Trim();
        targetLanguageCode = targetLanguageCode.Trim();
        if (text.EnumerateRunes().Count() is < 1 or > 5000
            || targetLanguageCode.EnumerateRunes().Count() is < 2 or > 10
            || remainingMilliseconds is < 1 or > 300_000)
        {
            throw new ArgumentException("Chat translation request is invalid.");
        }

        string prompt = $"""
            You are a chat message translator.

            Translate only the message between <message> and </message> into the target language.

            Target language code: {targetLanguageCode}
            Source language code: auto

            Rules:
            - Return only the translated message.
            - Do not return JSON.
            - Do not include explanations.
            - Do not include labels.
            - Preserve names, URLs, emails, emojis, numbers, code, markdown, and placeholders.
            - If the message is already in the target language, return it as-is.

            <message>
            {text}
            </message>
            """;

        return new(Guid.NewGuid().ToString("N"), SystemInstruction, [new("user", prompt)],
            "LUNA", "none", "low", 1024, remainingMilliseconds, 1, null, null, false,
            "CHAT_MESSAGE_TRANSLATION");
    }

    public static string Normalize(JsonElement output)
    {
        // 기존 Python 정제기의 fence와 JSON wrapper 순서를 유지한다.
        if (output.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("AI translation response is empty.");
        }

        string cleaned = output.GetString()!.Trim();
        if (cleaned.StartsWith("```", StringComparison.Ordinal))
        {
            cleaned = cleaned.Trim('`').Trim();
            if (cleaned.StartsWith("json", StringComparison.Ordinal))
            {
                cleaned = cleaned[4..].Trim();
            }
        }

        cleaned = cleaned.Trim().Trim('"');
        try
        {
            using var parsed = JsonDocument.Parse(cleaned);
            if (parsed.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (string key in new[] { "translated_text", "translatedText", "text" })
                {
                    if (parsed.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        return value.GetString()!.Trim();
                    }
                }
            }

            if (parsed.RootElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(parsed.RootElement.GetString()))
            {
                return parsed.RootElement.GetString()!.Trim();
            }
        }
        catch (JsonException)
        {
            // plain text도 원본 계약에서 유효한 번역 결과다.
        }

        return cleaned;
    }
}
