namespace TranslaCat.Chat.Application.NotificationPreferences;

public sealed record ChatNotificationPreference(bool PushEnabled);
public sealed record ChatRoomNotificationPreference(long RoomId, bool Muted);

public interface IChatNotificationPreferenceStore
{
    Task<ChatNotificationPreference> GetAsync(long userId, CancellationToken token);
    Task<ChatNotificationPreference> PutAsync(long userId, bool pushEnabled, CancellationToken token);
    Task<ChatRoomNotificationPreference?> GetRoomAsync(long userId, long roomId, CancellationToken token);
    Task<ChatRoomNotificationPreference?> PutRoomAsync(long userId, long roomId, bool muted, CancellationToken token);
}

public sealed class ChatNotificationPreferenceException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
public sealed class ChatNotificationPreferenceUnavailableException : Exception;

public sealed class ChatNotificationPreferenceService(IChatNotificationPreferenceStore store)
{
    public Task<ChatNotificationPreference> GetAsync(long userId, CancellationToken token = default)
    {
        RequireActor(userId);
        return store.GetAsync(userId, token);
    }

    public Task<ChatNotificationPreference> PutAsync(long userId, bool pushEnabled, CancellationToken token = default)
    {
        RequireActor(userId);
        return store.PutAsync(userId, pushEnabled, token);
    }

    public async Task<ChatRoomNotificationPreference> GetRoomAsync(long userId, long roomId, CancellationToken token = default)
    {
        RequireRoom(userId, roomId);
        return await store.GetRoomAsync(userId, roomId, token)
            ?? throw new ChatNotificationPreferenceException("CHAT_NOTIFICATION_PREFERENCE_NOT_ALLOWED");
    }

    public async Task<ChatRoomNotificationPreference> PutRoomAsync(long userId, long roomId, bool muted, CancellationToken token = default)
    {
        RequireRoom(userId, roomId);
        return await store.PutRoomAsync(userId, roomId, muted, token)
            ?? throw new ChatNotificationPreferenceException("CHAT_NOTIFICATION_PREFERENCE_NOT_ALLOWED");
    }

    private static void RequireActor(long userId)
    {
        if (userId <= 0) throw new UnauthorizedAccessException();
    }

    private static void RequireRoom(long userId, long roomId)
    {
        RequireActor(userId);
        if (roomId <= 0) throw new ChatNotificationPreferenceException("CHAT_NOTIFICATION_PREFERENCE_INVALID_REQUEST");
    }
}
