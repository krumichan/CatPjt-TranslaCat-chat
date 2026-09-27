using System.Globalization;
using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Application.OpenRooms;

public static class OpenRoomPolicy
{
    public const int MemberCodeAttempts = 20;
    public const string MemberCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const string ImagePrefix = "open-chat-profiles/";

    public static OpenRoomValidatedCreate ValidateCreate(OpenRoomCreate? request)
    {
        if (request is null)
        {
            throw Error("OPEN 채팅방 생성 요청은 필수입니다.", "REQUEST_REQUIRED");
        }

        // 원본은 Java isBlank 후 trim된 UTF-16 길이를 검사한다.
        var name = Required(request.Name, 100, "OPEN 채팅방 이름", "NAME");
        var description = Required(request.Description, 500, "OPEN 채팅방 설명", "DESCRIPTION");
        if (request.Visibility is not ("PUBLIC" or "UNLISTED"))
        {
            throw Error("OPEN 채팅방 공개 범위는 필수입니다.", "VISIBILITY_REQUIRED");
        }
        var count = request.MaxMemberCount ?? 50;
        if (count is < 2 or > 100)
        {
            throw Error("OPEN 채팅방 최대 인원은 2명 이상 100명 이하여야 합니다.", "MAX_MEMBER_COUNT_INVALID");
        }
        if (request.OwnerProfile is null)
        {
            throw Error("OWNER OPEN 프로필은 필수입니다.", "OWNER_PROFILE_REQUIRED");
        }

        return new(name, description, request.Visibility, count, NormalizeNickname(request.OwnerProfile.Nickname),
            NormalizeObjectKey(request.OwnerProfile.ProfileImageObjectKey));
    }

    public static string NormalizeNickname(string? nickname)
    {
        return Required(nickname, 50, "OPEN 채팅 닉네임", "NICKNAME");
    }

    public static string? NormalizeObjectKey(string? objectKey, long? memberId = null)
    {
        if (ChatMessageText.IsBlank(objectKey))
        {
            return null;
        }

        var normalized = ChatMessageText.Trim(objectKey!);
        var prefix = memberId is > 0 ? ImagePrefix + memberId.Value.ToString(CultureInfo.InvariantCulture) + "/" : ImagePrefix;
        if (normalized.Length > 500 || !normalized.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw Error("OPEN 채팅 프로필 이미지 Object Key가 유효하지 않습니다.", "PROFILE_IMAGE_OBJECT_KEY_INVALID");
        }
        return normalized;
    }

    public static string CreateMemberCodeCandidate(Func<int, int> nextIndex)
    {
        return "OC-" + new string(Enumerable.Range(0, 5)
            .Select(_ => MemberCodeAlphabet[nextIndex(MemberCodeAlphabet.Length)]).ToArray());
    }

    public static string? NormalizeKeyword(string? keyword)
    {
        if (ChatMessageText.IsBlank(keyword))
        {
            return null;
        }
        var normalized = ChatMessageText.Trim(keyword!);
        if (normalized.Length > 100)
        {
            throw Error("OPEN 채팅 검색어는 100자 이하여야 합니다.", "KEYWORD_TOO_LONG");
        }
        return normalized;
    }

    public static string JoinBlockedReason(bool banned, bool closed, bool joined, long memberCount, int maximum)
    {
        return banned ? "BANNED" : closed ? "ROOM_CLOSED" : joined ? "ALREADY_JOINED" : memberCount >= maximum ? "ROOM_FULL" : "NONE";
    }

    public static OpenRoomException Error(string message, string suffix)
    {
        return new(message, "OPEN_CHAT_" + suffix);
    }

    private static string Required(string? value, int maximum, string label, string code)
    {
        if (ChatMessageText.IsBlank(value))
        {
            throw Error(label + "은 필수입니다.", code + "_REQUIRED");
        }
        var normalized = ChatMessageText.Trim(value!);
        if (normalized.Length > maximum)
        {
            throw Error($"{label}은 {maximum}자 이하여야 합니다.", code + "_TOO_LONG");
        }
        return normalized;
    }
}
