using System.Net;

namespace TranslaCat.Chat.ApiTests.Language;

public sealed class ChatLegacyLanguageHttpTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"originalLanguageCode\":null,\"translationLanguageCode\":\"ja\",\"showOriginal\":true}")]
    [InlineData("{\"originalLanguageCode\":\"   \",\"translationLanguageCode\":\"ja\",\"showOriginal\":true}")]
    [InlineData("{\"originalLanguageCode\":\"12345678901\",\"translationLanguageCode\":\"ja\",\"showOriginal\":true}")]
    [InlineData("{\"originalLanguageCode\":\"en\",\"translationLanguageCode\":\"ja\",\"showOriginal\":1.0}")]
    [InlineData("null")]
    public async Task Legacy_DTO_keeps_NotBlank_raw_length_and_binding_errors_as_500(string body)
    {
        // 준비
        await using var host = await LanguageHttpHost.StartAsync();

        // 실행
        using var response = await host.SendAsync("PATCH", "/api/v1/chat/rooms/41/members/me/language", body);

        // 검증
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"originalLanguageCode\":\"en\",\"translationLanguageCode\":\"ja\"}")]
    [InlineData("{\"originalLanguageCode\":\"en\",\"translationLanguageCode\":\"ja\",\"showOriginal\":null,\"showTranslation\":null}")]
    public async Task Missing_or_null_primitive_flags_default_to_false_and_keep_business_validation(string body)
    {
        // 준비
        await using var host = await LanguageHttpHost.StartAsync();

        // 실행
        using var response = await host.SendAsync("PATCH", "/api/v1/chat/rooms/41/members/me/language", body);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("", json.GetProperty("body").GetProperty("errorCode").GetString());
        Assert.Equal("Message <원문 또는 번역 중 최소 하나는 표시해야 합니다.>", json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Legacy_reset_returns_raw_member_true_flags_even_when_personal_defaults_are_false()
    {
        // 준비
        await using var host = await LanguageHttpHost.StartAsync();
        using var defaults = await host.SendAsync("PATCH", "/api/v1/users/me/chat-language-settings",
            "{\"originalLanguageCode\":\"fr\",\"translationLanguageCode\":\"de\",\"showOriginal\":false,\"showTranslation\":false}");
        Assert.Equal(HttpStatusCode.OK, defaults.StatusCode);

        // 실행
        using var update = await host.SendAsync("PATCH", "/api/v1/chat/rooms/41/members/me/language",
            "{\"originalLanguageCode\":\" EN \",\"translationLanguageCode\":\" JA \",\"showOriginal\":true}");
        using var reset = await host.SendAsync("DELETE", "/api/v1/chat/rooms/41/members/me/language", null);
        using var read = await host.SendAsync("GET", "/api/v1/chat/rooms/41/members/me/language", null);
        var changed = (await ReadHttpFixture.ReadJsonAsync(update)).GetProperty("body");
        var restored = (await ReadHttpFixture.ReadJsonAsync(reset)).GetProperty("body");
        var fetched = (await ReadHttpFixture.ReadJsonAsync(read)).GetProperty("body");

        // 검증
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("en", changed.GetProperty("originalLanguageCode").GetString());
        Assert.False(changed.GetProperty("showTranslation").GetBoolean());
        Assert.Equal("fr", restored.GetProperty("originalLanguageCode").GetString());
        Assert.True(restored.GetProperty("showOriginal").GetBoolean());
        Assert.True(restored.GetProperty("showTranslation").GetBoolean());
        Assert.False(restored.GetProperty("roomLanguageSettingApplied").GetBoolean());
        Assert.False(restored.TryGetProperty("source", out _));
        Assert.Equal(restored.GetRawText(), fetched.GetRawText());
    }

    [Fact]
    public async Task Legacy_membership_error_keeps_member_specific_error_code()
    {
        // 준비
        await using var host = await LanguageHttpHost.StartAsync();

        // 실행
        using var response = await host.SendAsync("GET", "/api/v1/chat/rooms/42/members/me/language", null);
        var json = await ReadHttpFixture.ReadJsonAsync(response);

        // 검증
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("CHAT_ROOM_MEMBER_ACCESS_DENIED", json.GetProperty("body").GetProperty("errorCode").GetString());
    }
}
