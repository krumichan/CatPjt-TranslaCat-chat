using System.Text.Json;
using TranslaCat.Chat.Api.Messaging;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.ApiTests.Messaging;

public sealed class ChatMessageContractTests
{
    [Fact]
    public void Message_wire_preserves_all_fields_long_precision_nullable_translation_and_UTC_time()
    {
        // 준비
        var value = SyntheticMessageTransaction.View() with
        {
            Id = 9007199254740993L,
            UnreadMemberCount = null
        };
        var mapper = new ChatMessageContractMapper(TimeZoneInfo.CreateCustomTimeZone("Synthetic+09", TimeSpan.FromHours(9), "Synthetic+09", "Synthetic+09"));

        // 실행
        var json = JsonSerializer.SerializeToElement(mapper.ToResponse(value));

        // 검증
        Assert.Equal(16, json.EnumerateObject().Count());
        Assert.Equal(9007199254740993L, json.GetProperty("id").GetInt64());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("senderAiMemberId").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("unreadMemberCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("sender").ValueKind);
        Assert.Equal("2026-09-26T03:34:56Z", json.GetProperty("createdAt").GetString());
        var translation = Assert.Single(json.GetProperty("translations").EnumerateArray());
        Assert.Equal(6, translation.EnumerateObject().Count());
        Assert.Equal("PENDING", translation.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, translation.GetProperty("completedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, translation.GetProperty("translatedContent").ValueKind);
    }

    [Fact]
    public void OPEN_sender_and_created_event_keep_anonymous_outer_fields_and_member_identity()
    {
        // 준비
        var value = SyntheticMessageTransaction.View() with
        {
            SenderUserId = null,
            SenderName = null,
            SenderEmail = null,
            Sender = new OpenChatMessageSenderView(901, "ABC123", "합성 OPEN 이름", null, "MEMBER")
        };
        var mapper = new ChatMessageContractMapper(TimeZoneInfo.Utc);

        // 실행
        var json = JsonSerializer.SerializeToElement(mapper.ToCreatedEvent(value, SyntheticMessageTransaction.LocalNow));

        // 검증
        Assert.Equal("chat.message.created", json.GetProperty("eventType").GetString());
        Assert.Equal("2026-09-26T12:34:56Z", json.GetProperty("occurredAt").GetString());
        var message = json.GetProperty("message");
        Assert.Equal(JsonValueKind.Null, message.GetProperty("senderUserId").ValueKind);
        Assert.Equal(JsonValueKind.Null, message.GetProperty("senderEmail").ValueKind);
        var sender = message.GetProperty("sender");
        Assert.Equal(5, sender.EnumerateObject().Count());
        Assert.Equal(901, sender.GetProperty("openChatMemberId").GetInt64());
        Assert.Equal("ABC123", sender.GetProperty("memberCode").GetString());
    }
}
