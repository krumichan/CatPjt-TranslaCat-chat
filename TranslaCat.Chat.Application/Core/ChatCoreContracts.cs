using System.Text.Json.Serialization;

namespace TranslaCat.Chat.Application.Core;

public interface IChatCoreAccessTokenProvider
{
    // 사용자 ID를 합성하지 않는다. operation 하나에 한정된 CHAT 서비스 토큰이다.
    string CreateToken(string scope);
}

public sealed record ChatCoreHttpSettings(Uri BaseUri, TimeSpan Timeout);
public sealed class ChatCoreUnavailableException() : Exception("Chat Core dependency is unavailable.");

public sealed record ChatCoreProfile(
    [property: JsonRequired] string? Nickname,
    [property: JsonRequired] string? ProfileImageUrl,
    [property: JsonRequired] string? ProfileBackgroundImageUrl,
    [property: JsonRequired] string? Bio);

public sealed record ChatCoreSummary(
    [property: JsonRequired] long UserId,
    [property: JsonRequired] string PublicId,
    [property: JsonRequired] string Nickname,
    [property: JsonRequired] string? ProfileImageUrl,
    [property: JsonRequired] string? ProfileBackgroundImageUrl,
    [property: JsonRequired] string? Bio);

public sealed record ChatCoreAccount(
    [property: JsonRequired] long UserId,
    [property: JsonRequired] string Email,
    [property: JsonRequired] string? Username,
    [property: JsonRequired] string PublicId,
    [property: JsonRequired] ChatCoreProfile? Profile,
    [property: JsonRequired] ChatCoreSummary Summary);

public sealed record ChatCoreAccounts(
    [property: JsonRequired] IReadOnlyList<ChatCoreAccount> Accounts,
    [property: JsonRequired] IReadOnlyList<long> MissingUserIds,
    [property: JsonRequired] IReadOnlyList<string> MissingPublicIds);

public sealed record ChatCoreRelation(
    [property: JsonRequired] long TargetUserId,
    [property: JsonRequired] bool Friends,
    [property: JsonRequired] bool Blocked,
    [property: JsonRequired] string FriendStatus);

public sealed record ChatCoreRelations(
    [property: JsonRequired] IReadOnlyList<ChatCoreRelation> Relations,
    [property: JsonRequired] IReadOnlyList<long> MissingUserIds);

public sealed record ChatCoreObjectUrl(
    [property: JsonRequired] string ObjectKey,
    [property: JsonRequired] string? Url);
public sealed record ChatCoreObjectUrls([property: JsonRequired] IReadOnlyList<ChatCoreObjectUrl> Objects);
