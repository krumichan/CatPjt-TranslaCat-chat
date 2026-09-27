using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Application.AiManagement;

public sealed record ChatAiProfileInput(string? Nickname, string? Bio, string? OriginalLanguageCode, string? PersonaPrompt);
public sealed record ChatAiProfile(string Nickname, string? Bio, string OriginalLanguageCode, string PersonaPrompt);
public sealed record ChatAiMember(long AiMemberId, long AiAgentId, long ChatRoomId, string Nickname,
    string? ProfileImageUrl, string? ProfileBackgroundImageUrl, string? Bio, string OriginalLanguageCode,
    string PersonaPrompt, bool Active, DateTime JoinedAt, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record ChatAiMemberList(long ChatRoomId, int CurrentCount, int MaxCount, IReadOnlyList<ChatAiMember> Members);
public sealed record ChatAiRoomSettings(long ChatRoomId, bool AiEnabled, int CurrentAiMemberCount, int MaxAiMembersPerRoom,
    string DisclosureType, string MentionPermission, bool ConversationEnabled, bool RevivalEnabled);
public sealed record ChatAiRoomPatch(string? DisclosureType, string? MentionPermission, bool? ConversationEnabled, bool? RevivalEnabled);

public interface IChatAiProfileStorage
{
    Task<string?> ResolveUrlAsync(string objectKey, CancellationToken token);
}

public interface IChatAiManagementStore
{
    Task<ChatAiMemberList> ListAsync(long userId, long roomId, DateTime now, CancellationToken token);
    Task<ChatAiMember> GetAsync(long userId, long roomId, long memberId, bool safeProfile, CancellationToken token);
    Task<ChatAiMember> CreateAsync(long userId, long roomId, ChatAiProfileInput? profile, DateTime now, CancellationToken token);
    Task<ChatAiMember> UpdateAsync(long userId, long roomId, long memberId, ChatAiProfileInput? profile, DateTime now, CancellationToken token);
    Task<ChatAiMember> DeleteAsync(long userId, long roomId, long memberId, DateTime now, CancellationToken token);
    Task<ChatAiRoomSettings> RoomSettingsAsync(long userId, long roomId, ChatAiRoomPatch? patch, DateTime now, CancellationToken token);
    Task<ChatAiSystemSettings> SystemSettingsAsync(long userId, ChatAiSystemPatch? patch, DateTime now, CancellationToken token);
}

public static class ChatAiProfilePolicy
{
    public static ChatAiProfile Normalize(ChatAiProfileInput input)
    {
        // Java isBlank/trim과 UTF-16 길이를 유지하며 persona를 공개 안전 프로필에 노출하지 않는다.
        var nickname = Required(input.Nickname, 50, "AI 닉네임은 필수입니다.", "AI 닉네임은 50자 이하여야 합니다.", "NICKNAME");
        var bio = ChatMessageText.IsBlank(input.Bio) ? null : ChatMessageText.Trim(input.Bio!);
        if (bio?.Length > 200)
        {
            throw Error("AI 자기소개는 200자 이하여야 합니다.", "BIO_TOO_LONG");
        }

        var language = Required(input.OriginalLanguageCode, 10,
            "AI 원문 언어는 필수입니다.", "AI 원문 언어 코드는 10자 이하여야 합니다.", "LANGUAGE").ToLowerInvariant();
        var persona = Required(input.PersonaPrompt, 4000,
            "AI personaPrompt는 필수입니다.", "AI personaPrompt는 4000자 이하여야 합니다.", "PERSONA");
        return new(nickname, bio, language, persona);
    }

    private static string Required(string? value, int maximum, string requiredMessage, string tooLongMessage, string code)
    {
        if (ChatMessageText.IsBlank(value))
        {
            throw Error(requiredMessage, code + "_REQUIRED");
        }

        var result = ChatMessageText.Trim(value!);
        if (result.Length > maximum)
        {
            throw Error(tooLongMessage, code + "_TOO_LONG");
        }

        return result;
    }

    public static ChatAiManagementException Error(string message, string code)
    {
        return new(message, "CHAT_AI_" + code);
    }
}
