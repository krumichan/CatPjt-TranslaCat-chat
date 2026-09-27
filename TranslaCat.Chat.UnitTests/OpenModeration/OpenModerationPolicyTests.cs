using TranslaCat.Chat.Application.OpenModeration;
using TranslaCat.Chat.Application.OpenRooms;

namespace TranslaCat.Chat.UnitTests.OpenModeration;

public sealed class OpenModerationPolicyTests
{
    [Theory]
    [InlineData("OWNER", "OWNER", "ADMIN", true)]
    [InlineData("OWNER", "ADMIN", "MEMBER", true)]
    [InlineData("ADMIN", "ADMIN", "MEMBER", true)]
    [InlineData("ADMIN", "OWNER", "MEMBER", false)]
    [InlineData("ADMIN", "ADMIN", "ADMIN", false)]
    [InlineData("MEMBER", "ADMIN", "MEMBER", false)]
    public void Release_uses_actor_role_and_historical_ban_roles(string actor, string bannedBy, string target, bool expected)
    {
        // 준비 / 실행
        var allowed = OpenModerationPolicy.CanRelease(actor, bannedBy, target);

        // 검증
        Assert.Equal(expected, allowed);
    }

    [Theory]
    [InlineData(1, "OWNER", 1, "OWNER", "OPEN_CHAT_BAN_SELF_NOT_ALLOWED")]
    [InlineData(1, "ADMIN", 2, "OWNER", "OPEN_CHAT_BAN_ROLE_FORBIDDEN")]
    [InlineData(1, "ADMIN", 2, "ADMIN", "OPEN_CHAT_BAN_ROLE_FORBIDDEN")]
    public void Ban_keeps_self_and_role_rejection_order(long actor, string actorRole, long target, string targetRole, string code)
    {
        // 준비 / 실행
        var error = Assert.Throws<OpenRoomException>(() => OpenModerationPolicy.ValidateBanTarget(actor, actorRole, target, targetRole));

        // 검증
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public void Reason_preserves_Java_blank_trim_and_UTF16_length()
    {
        // 준비
        string accepted = new('x', 500);

        // 실행
        string reason = OpenModerationPolicy.NormalizeReason(" " + accepted + " ");
        var blank = Assert.Throws<OpenRoomException>(() => OpenModerationPolicy.NormalizeReason("\u3000"));
        var length = Assert.Throws<OpenRoomException>(() => OpenModerationPolicy.NormalizeReason(accepted + "x"));

        // 검증
        Assert.Equal(accepted, reason);
        Assert.Equal("OPEN_CHAT_BAN_REASON_REQUIRED", blank.Code);
        Assert.Equal("OPEN_CHAT_BAN_REASON_TOO_LONG", length.Code);
    }
}
