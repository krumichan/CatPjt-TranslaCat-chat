using System.Globalization;

namespace TranslaCat.Chat.Application.Language;

public sealed record ChatLanguageValues(string OriginalLanguageCode, string TranslationLanguageCode, bool ShowOriginal, bool ShowTranslation);
public sealed record ChatLanguageUpdate(string? OriginalLanguageCode, string? TranslationLanguageCode, bool? ShowOriginal, bool? ShowTranslation);
public sealed record ChatLanguageResult(ChatLanguageValues Values, bool RoomLanguageSettingApplied, string Source);
public sealed record ChatRoomLanguageState(long MemberId, string? OriginalLanguageCode, string? TranslationLanguageCode, bool ShowOriginal, bool ShowTranslation);

public interface IChatLanguageStore
{
    Task<T> ExecuteAsync<T>(bool write, Func<IChatLanguageSession, CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}

public interface IChatLanguageSession
{
    Task<ChatLanguageValues?> GetDefaultAsync(long userId, CancellationToken cancellationToken);
    Task<ChatRoomLanguageState> GetRoomAsync(long userId, long roomId, CancellationToken cancellationToken);
    Task SaveDefaultAsync(long userId, ChatLanguageValues value, CancellationToken cancellationToken);
    Task SaveRoomAsync(long userId, long memberId, ChatLanguageValues? value, CancellationToken cancellationToken);
}

public sealed class ChatLanguageException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ChatLanguageDependencyUnavailableException : Exception;

public sealed class ChatLanguageService(IChatLanguageStore store)
{
    public static ChatLanguageValues SystemDefault { get; } = new("ko", "ja", true, true);

    public Task<ChatLanguageResult> GetDefaultAsync(long userId, CancellationToken cancellationToken = default)
    {
        return store.ExecuteAsync(false, (session, token) => ResolveDefaultAsync(session, userId, token), cancellationToken);
    }

    public Task<ChatLanguageResult> UpdateDefaultAsync(long userId, ChatLanguageUpdate request, CancellationToken cancellationToken = default)
    {
        return store.ExecuteAsync(true, async (session, token) =>
            {
                // 개인 기본 PATCH의 누락 필드는 기존 값이 아니라 시스템 기본값으로 돌아간다.
                var values = Merge(request, SystemDefault);
                await session.SaveDefaultAsync(userId, values, token);
                return new ChatLanguageResult(values, false, "DEFAULT");
            }, cancellationToken);
    }

    public Task<ChatLanguageResult> GetRoomAsync(long userId, long roomId, CancellationToken cancellationToken = default)
    {
        return store.ExecuteAsync(false, async (session, token) =>
            {
                var room = await session.GetRoomAsync(userId, roomId, token);
                return ResolveRoom(room, await ResolveDefaultAsync(session, userId, token));
            }, cancellationToken);
    }

    public Task<ChatLanguageResult> UpdateRoomAsync(long userId, long roomId, ChatLanguageUpdate request, CancellationToken cancellationToken = default)
    {
        return store.ExecuteAsync(true, async (session, token) =>
            {
                // 방별 PATCH는 현재 유효한 override/default 값을 기준으로 누락 필드를 보존한다.
                var room = await session.GetRoomAsync(userId, roomId, token);
                var current = ResolveRoom(room, await ResolveDefaultAsync(session, userId, token));
                var values = Merge(request, current.Values);
                await session.SaveRoomAsync(userId, room.MemberId, values, token);
                return new ChatLanguageResult(values, true, "ROOM_OVERRIDE");
            }, cancellationToken);
    }

    public Task<ChatLanguageResult> ResetRoomAsync(long userId, long roomId, CancellationToken cancellationToken = default)
    {
        return store.ExecuteAsync(true, async (session, token) =>
            {
                var room = await session.GetRoomAsync(userId, roomId, token);
                await session.SaveRoomAsync(userId, room.MemberId, null, token);
                return await ResolveDefaultAsync(session, userId, token);
            }, cancellationToken);
    }

    private static async Task<ChatLanguageResult> ResolveDefaultAsync(IChatLanguageSession session, long userId, CancellationToken cancellationToken)
    {
        var stored = await session.GetDefaultAsync(userId, cancellationToken);
        return new(stored ?? SystemDefault, false, stored is null ? "SYSTEM" : "DEFAULT");
    }

    public static ChatLanguageResult ResolveRoom(ChatRoomLanguageState room, ChatLanguageResult fallback)
    {
        if (room.OriginalLanguageCode is null && room.TranslationLanguageCode is null)
        {
            return fallback;
        }
        return new(new(Normalize(room.OriginalLanguageCode) ?? fallback.Values.OriginalLanguageCode,
            Normalize(room.TranslationLanguageCode) ?? fallback.Values.TranslationLanguageCode,
            room.ShowOriginal, room.ShowTranslation), true, "ROOM_OVERRIDE");
    }

    private static ChatLanguageValues Merge(ChatLanguageUpdate request, ChatLanguageValues fallback)
    {
        return new(Normalize(request.OriginalLanguageCode) ?? fallback.OriginalLanguageCode,
                Normalize(request.TranslationLanguageCode) ?? fallback.TranslationLanguageCode,
                request.ShowOriginal ?? fallback.ShowOriginal, request.ShowTranslation ?? fallback.ShowTranslation);
    }

    private static string? Normalize(string? value)
    {
        // Java isBlank와 trim의 서로 다른 공백 범위를 유지한다. 임의 지원 언어 목록 검증은 추가하지 않는다.
        if (value is null || value.All(IsJavaWhitespace))
        {
            return null;
        }
        int first = 0, last = value.Length - 1;
        while (first <= last && value[first] <= '\u0020')
        {
            first++;
        }
        while (last >= first && value[last] <= '\u0020')
        {
            last--;
        }
        return value[first..(last + 1)].ToLowerInvariant();
    }

    private static bool IsJavaWhitespace(char value)
    {
        var category = char.GetUnicodeCategory(value);
        return value is (>= '\u0009' and <= '\u000d') or (>= '\u001c' and <= '\u001f')
            || (category is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                && value is not ('\u00a0' or '\u2007' or '\u202f'));
    }
}
