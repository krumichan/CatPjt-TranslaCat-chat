using TranslaCat.Chat.Application.Messaging;

namespace TranslaCat.Chat.Application.Language;

public sealed record ChatLegacyLanguageUpdate(string OriginalLanguageCode, string TranslationLanguageCode, bool ShowOriginal, bool ShowTranslation);

public sealed class ChatLegacyMemberLanguageService(IChatLanguageStore store)
{
    public Task<ChatLanguageResult> GetAsync(long userId, long roomId, CancellationToken cancellationToken = default)
    {
        return store.ExecuteAsync(false, async (session, token) =>
            {
                var room = await GetMemberAsync(session, userId, roomId, token);
                return await ResolveAsync(session, userId, room, token);
            }, cancellationToken);
    }

    public Task<ChatLanguageResult> UpdateAsync(long userId, long roomId, ChatLegacyLanguageUpdate request, CancellationToken cancellationToken = default)
    {
        return store.ExecuteAsync(true, async (session, token) =>
            {
                // 구형 경로는 primitive boolean의 기본 false와 최소 하나 표시 조건을 보존한다.
                if (!request.ShowOriginal && !request.ShowTranslation)
                {
                    throw new ChatLanguageException("원문 또는 번역 중 최소 하나는 표시해야 합니다.", "");
                }
                var room = await GetMemberAsync(session, userId, roomId, token);
                var stored = new ChatLanguageValues(ChatMessageText.Trim(request.OriginalLanguageCode),
                    ChatMessageText.Trim(request.TranslationLanguageCode), request.ShowOriginal, request.ShowTranslation);
                await session.SaveRoomAsync(userId, room.MemberId, stored, token);

                // 저장은 trim만 한다. 응답은 원본 resolver처럼 정규화하며 일반 PATCH의 merge 정책을 섞지 않는다.
                var changed = new ChatRoomLanguageState(room.MemberId, stored.OriginalLanguageCode,
                    stored.TranslationLanguageCode, stored.ShowOriginal, stored.ShowTranslation);
                return await ResolveAsync(session, userId, changed, token);
            }, cancellationToken);
    }

    public Task<ChatLanguageResult> ResetAsync(long userId, long roomId, CancellationToken cancellationToken = default)
    {
        return store.ExecuteAsync(true, async (session, token) =>
            {
                var room = await GetMemberAsync(session, userId, roomId, token);
                await session.SaveRoomAsync(userId, room.MemberId, null, token);
                return await ResolveAsync(session, userId, new(room.MemberId, null, null, true, true), token);
            }, cancellationToken);
    }

    private static async Task<ChatRoomLanguageState> GetMemberAsync(IChatLanguageSession session, long userId, long roomId, CancellationToken token)
    {
        try
        {
            return await session.GetRoomAsync(userId, roomId, token);
        }
        catch (ChatLanguageException exception) when (exception.Code == "CHAT_ROOM_ACCESS_DENIED")
        {
            // 동일 저장 조회라도 구형 member API의 공개 business code는 신규 언어 API와 다르다.
            throw new ChatLanguageException("채팅방 멤버가 아니거나 접근 권한이 없습니다.", "CHAT_ROOM_MEMBER_ACCESS_DENIED");
        }
    }

    private static async Task<ChatLanguageResult> ResolveAsync(IChatLanguageSession session, long userId, ChatRoomLanguageState room, CancellationToken token)
    {
        var defaults = await session.GetDefaultAsync(userId, token);
        var resolved = ChatLanguageService.ResolveRoom(room,
            new(defaults ?? ChatLanguageService.SystemDefault, false, defaults is null ? "SYSTEM" : "DEFAULT"));

        // 구형 DTO는 유효 코드와 함께 raw member 표시 플래그를 반환한다. reset 후 개인 기본 false와 다를 수 있다.
        return resolved with
        {
            Values = resolved.Values with
            {
                ShowOriginal = room.ShowOriginal,
                ShowTranslation = room.ShowTranslation
            }
        };
    }
}
