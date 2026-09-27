using TranslaCat.Chat.Application.OpenRooms;

namespace TranslaCat.Chat.UnitTests.OpenRooms;

public sealed class OpenRoomPolicyTests
{
    [Fact]
    public void Create_normalizes_required_text_and_keeps_default_capacity()
    {
        // 준비
        var request = new OpenRoomCreate(" name ", " description ", "PUBLIC", null, new(" nickname ", "  "));

        // 실행
        var normalized = OpenRoomPolicy.ValidateCreate(request);

        // 검증
        Assert.Equal("name", normalized.Name);
        Assert.Equal("description", normalized.Description);
        Assert.Equal("nickname", normalized.Nickname);
        Assert.Equal(50, normalized.MaxMemberCount);
        Assert.Null(normalized.ProfileImageObjectKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\n")]
    [InlineData("\u3000")]
    public void Java_blank_nickname_is_rejected(string? nickname)
    {
        // 준비 / 실행
        var error = Assert.Throws<OpenRoomException>(() => OpenRoomPolicy.NormalizeNickname(nickname));

        // 검증
        Assert.Equal("OPEN_CHAT_NICKNAME_REQUIRED", error.Code);
    }

    [Fact]
    public void Nonbreaking_space_is_not_redefined_as_Java_blank()
    {
        // 준비
        const string nickname = "\u00a0";

        // 실행
        var normalized = OpenRoomPolicy.NormalizeNickname(nickname);

        // 검증
        Assert.Equal(nickname, normalized);
    }

    [Fact]
    public void Nickname_length_uses_UTF16_units()
    {
        // 준비
        var valid = string.Concat(Enumerable.Repeat("😺", 25));

        // 실행
        var normalized = OpenRoomPolicy.NormalizeNickname(valid);
        var error = Assert.Throws<OpenRoomException>(() => OpenRoomPolicy.NormalizeNickname(valid + "x"));

        // 검증
        Assert.Equal(valid, normalized);
        Assert.Equal("OPEN_CHAT_NICKNAME_TOO_LONG", error.Code);
    }

    [Theory]
    [InlineData("open-chat-profiles/12/a.png", 12, true)]
    [InlineData("open-chat-profiles/123/a.png", 12, false)]
    [InlineData("user-profiles/12/a.png", 12, false)]
    public void Object_key_is_bound_to_the_exact_member_path(string key, long member, bool accepted)
    {
        // 준비 / 실행
        if (accepted)
        {
            var normalized = OpenRoomPolicy.NormalizeObjectKey(" " + key + " ", member);

            // 검증
            Assert.Equal(key, normalized);
        }
        else
        {
            var error = Assert.Throws<OpenRoomException>(() => OpenRoomPolicy.NormalizeObjectKey(key, member));

            // 검증
            Assert.Equal("OPEN_CHAT_PROFILE_IMAGE_OBJECT_KEY_INVALID", error.Code);
        }
    }

    [Fact]
    public void Member_code_uses_the_original_alphabet_and_five_random_characters()
    {
        // 준비
        int calls = 0;

        // 실행
        var code = OpenRoomPolicy.CreateMemberCodeCandidate(maximum =>
        {
            Assert.Equal(32, maximum);
            return calls++;
        });

        // 검증
        Assert.Equal("OC-ABCDE", code);
        Assert.Equal(5, calls);
        Assert.Equal(20, OpenRoomPolicy.MemberCodeAttempts);
    }

    [Theory]
    [InlineData(true, true, true, 100, "BANNED")]
    [InlineData(false, true, true, 100, "ROOM_CLOSED")]
    [InlineData(false, false, true, 100, "ALREADY_JOINED")]
    [InlineData(false, false, false, 100, "ROOM_FULL")]
    [InlineData(false, false, false, 99, "NONE")]
    public void Join_blocking_reason_keeps_source_precedence(bool banned, bool closed, bool joined, long count, string expected)
    {
        // 준비 / 실행
        var reason = OpenRoomPolicy.JoinBlockedReason(banned, closed, joined, count, 100);

        // 검증
        Assert.Equal(expected, reason);
    }
}
