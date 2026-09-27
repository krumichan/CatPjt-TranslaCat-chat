namespace TranslaCat.Chat.Application.Messaging;

public sealed record ChatMessageMember(long Id, long UserId, long ChatRoomId, string RoomType, DateTime JoinedAt);

public sealed record ChatStoredMessage(
    long Id, long ChatRoomId, long? SenderUserId, long? SenderAiMemberId,
    string SenderType, string MessageType, string Content, string Status,
    DateTime CreatedAt, DateTime UpdatedAt);

public sealed record ChatMessageTranslationView(
    long Id, string LanguageCode, string? TranslatedContent, string Status,
    string? FailureReason, DateTime? CompletedAt);

public sealed record OpenChatMessageSenderView(
    long OpenChatMemberId, string MemberCode, string Nickname,
    string? ProfileImageUrl, string Role);

public sealed record ChatMessageView(
    long Id, long ChatRoomId, long? SenderUserId, long? SenderAiMemberId,
    string? SenderName, string? SenderEmail, string? SenderProfileImageUrl,
    string SenderType, string MessageType, string Content, string Status,
    long? UnreadMemberCount, IReadOnlyList<ChatMessageTranslationView> Translations,
    DateTime CreatedAt, DateTime UpdatedAt, OpenChatMessageSenderView? Sender);

public sealed record ChatMessagePage(IReadOnlyList<ChatMessageView> Messages, long? NextCursorId, bool HasNext);

public sealed record ChatMessageAnchorPage(
    IReadOnlyList<ChatMessageView> Messages, long AnchorMessageId,
    long? PreviousCursorId, bool HasPrevious, long? NextCursorId, bool HasNext);

public sealed record ChatMessageLanguages(
    string SenderOriginalLanguageCode, IReadOnlyList<string?> MemberTranslationLanguageCodes);

public sealed record ChatMessageCreation(ChatStoredMessage Message, IReadOnlyList<long> TranslationIds);

// BE의 계정/일반 프로필과 스토리지 URL은 CHAT DB의 복제 테이블로 대체하지 않는다.
public sealed record ChatUserMessageProfile(long UserId, string? Name, string? Email, string? ProfileImageUrl);
