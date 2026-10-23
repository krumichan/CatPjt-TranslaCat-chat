using System.Text.Json;
using TranslaCat.Chat.Application.Safety;

namespace TranslaCat.Chat.UnitTests.Safety;

public sealed class ChatSafetyPolicyTests
{
    private static ChatReportCreateRequest Message() => new("MESSAGE", 51, null, null, "SPAM", "합성 설명", "d6e76159-879b-4e6e-a210-40d176d81cb1");

    [Theory]
    [InlineData("SPAM")]
    [InlineData("HARASSMENT")]
    [InlineData("HATE")]
    [InlineData("SEXUAL")]
    [InlineData("ILLEGAL")]
    [InlineData("PERSONAL_INFORMATION")]
    [InlineData("OTHER")]
    public void Recognized_reasons_accept_one_message_target(string reason)
    {
        // 준비
        var request = Message() with { Reason = reason };
        // 실행 / 검증
        ChatReportPolicy.ValidateCreate(7, 41, request);
    }

    [Theory]
    [InlineData("target-type")]
    [InlineData("mixed-target")]
    [InlineData("no-target")]
    [InlineData("negative-target")]
    [InlineData("bad-reason")]
    [InlineData("long-detail")]
    [InlineData("control")]
    [InlineData("uuid")]
    public void Invalid_or_ambiguous_reports_fail_before_store(string scenario)
    {
        // 준비
        var request = scenario switch
        {
            "target-type" => Message() with { TargetType = "ROOM" },
            "mixed-target" => Message() with { TargetUserId = 8 },
            "no-target" => Message() with { MessageId = null },
            "negative-target" => Message() with { MessageId = -1 },
            "bad-reason" => Message() with { Reason = "invalid" },
            "long-detail" => Message() with { Detail = new string('가', 2001) },
            "control" => Message() with { Detail = "x\0y" },
            _ => Message() with { ClientRequestId = "not-a-uuid" }
        };
        // 실행 / 검증
        Assert.Equal("CHAT_REPORT_INVALID", Assert.Throws<ChatSafetyException>(() => ChatReportPolicy.ValidateCreate(7, 41, request)).Code);
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("IN_REVIEW")]
    [InlineData("RESOLVED")]
    [InlineData("DISMISSED")]
    public void Explicit_review_states_are_supported_without_automatic_penalty(string status)
    {
        // 준비 / 실행 / 검증
        ChatReportPolicy.ValidateStatus(new(status, "합성 메모", 0));
    }

    [Theory]
    [InlineData("{\"targetType\":\"MESSAGE\",\"messageId\":1,\"reason\":\"SPAM\",\"clientRequestId\":\"d6e76159-879b-4e6e-a210-40d176d81cb1\",\"reporterUserId\":9}")]
    [InlineData("{\"messageId\":1,\"reason\":\"SPAM\",\"clientRequestId\":\"d6e76159-879b-4e6e-a210-40d176d81cb1\"}")]
    public void Json_contract_rejects_forged_actor_and_missing_required_fields(string json)
    {
        // 준비 / 실행 / 검증
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ChatReportCreateRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void Deletion_contract_requires_canonical_operation_and_utc_cutoff()
    {
        // 준비
        var valid = new ChatAccountDeletionRequest("d6e76159-879b-4e6e-a210-40d176d81cb1", DateTimeOffset.Parse("2026-10-23T00:00:00Z"), "synthetic@example.invalid");
        // 실행 / 검증
        ChatAccountDeletionPolicy.Validate(7, valid);
        Assert.Throws<ChatSafetyException>(() => ChatAccountDeletionPolicy.Validate(7, valid with { RequestedAtUtc = valid.RequestedAtUtc.ToOffset(TimeSpan.FromHours(9)) }));
        Assert.Throws<ChatSafetyException>(() => ChatAccountDeletionPolicy.Validate(0, valid));
        Assert.Throws<ChatSafetyException>(() => ChatAccountDeletionPolicy.Validate(7, valid with { AuditIdentity = new string('x', 321) }));
        Assert.Throws<ChatSafetyException>(() => ChatAccountDeletionPolicy.Validate(7, valid with { OperationId = "same-account" }));
    }
}
