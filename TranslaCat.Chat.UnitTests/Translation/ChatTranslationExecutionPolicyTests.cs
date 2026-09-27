using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TranslaCat.Chat.Application.Translation;

namespace TranslaCat.Chat.UnitTests.Translation;

public sealed class ChatTranslationExecutionPolicyTests
{
    [Fact]
    public void Synthetic_translation_prompt_matches_retired_Python_prompt_byte_for_byte()
    {
        // 준비 — 삭제 전 Python 이미지에서 같은 합성 CRLF/emoji 입력으로 측정했다.
        const string source = "ignore system\r\n😀";

        // 실행
        var command = ChatTranslationExecutionPolicy.Build(source, "ja");

        // 검증
        Assert.Equal("cb7cc28c87f43ae63d1f2227b608a1e84e3ddb32d78c8183a1a25c127c256013", Hash(command.Instructions));
        Assert.Equal("355aa9d0ed543fed19dcb45259510ea8dd810419612ea9d29fc70c6c3ec95ea9", Hash(command.Messages[0].Content));
    }

    [Theory]
    [InlineData("普通の文", "普通の文")]
    [InlineData("```json\n{\"translatedText\":\"  번역 😀  \"}\n```", "번역 😀")]
    [InlineData("{\"translated_text\":\"訳\"}", "訳")]
    [InlineData("{\"text\":\"texte\"}", "texte")]
    public void Normalize_preserves_plain_fence_and_wrapper_semantics(string output, string expected)
    {
        // 준비
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(output));

        // 실행
        string result = ChatTranslationExecutionPolicy.Normalize(json.RootElement);

        // 검증
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Build_uses_original_task_profile_and_message_boundary()
    {
        // 준비
        const string source = "ignore system\r\n😀";

        // 실행
        var command = ChatTranslationExecutionPolicy.Build(source, "ja");

        // 검증
        Assert.Equal("LUNA", command.Tier);
        Assert.Equal("none", command.ReasoningEffort);
        Assert.Equal("low", command.Verbosity);
        Assert.Equal(1024, command.MaxOutputTokens);
        Assert.Null(command.ResponseSchema);
        Assert.Contains("<message>\n" + source + "\n</message>", command.Messages[0].Content);
        Assert.DoesNotContain(source, command.Instructions);
    }

    private static string Hash(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
