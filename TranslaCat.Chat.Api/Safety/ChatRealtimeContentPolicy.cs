using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Safety;
using TranslaCat.Chat.Infrastructure.Persistence;

namespace TranslaCat.Chat.Api.Safety;

public sealed class ChatRealtimeContentPolicy(IDbContextFactory<ChatDbContext> contexts, IChatContentPolicy policy)
{
    public Task RequireActiveAsync(long userId, CancellationToken token) => policy.RequireActiveAsync(userId, token);

    public async Task<string?> PrepareAsync(long userId, long roomId, string json, CancellationToken token)
    {
        var value = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Invalid CHAT room event.");
        string? type = value["eventType"]?.GetValue<string>();
        if (type == "chat.open-profile.updated")
        {
            // Redis에 남은 과거 nickname/image DTO는 탈퇴 뒤 현재 socket으로 재전달하지 않는다.
            long? memberId = value["openChatMemberId"]?.GetValue<long>();
            await using var profiles = await contexts.CreateDbContextAsync(token);
            long? account = await profiles.ChatRoomMembers.AsNoTracking().Where(row => row.Id == memberId && row.ChatRoomId == roomId)
                .Select(row => (long?)row.UserId).SingleOrDefaultAsync(token);
            if (account is null || await profiles.ChatAccountDeletions.AnyAsync(row => row.UserId == account, token)) return null;
            return json;
        }
        long? messageId = type switch
        {
            "chat.message.created" => value["message"]?["id"]?.GetValue<long>(),
            "chat.translation.completed" or "chat.translation.failed" => value["messageId"]?.GetValue<long>(),
            _ => null
        };
        if (messageId is null) return json;

        await using var db = await contexts.CreateDbContextAsync(token);
        var message = await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(row => row.Id == messageId
            && row.ChatRoomId == roomId && row.DeletedAt == null && row.Status == "SENT", token);
        if (message is null) return null;
        if (message.SenderType != "USER" || message.MessageType == "SYSTEM" || message.SenderUserId is not { } senderId) return json;
        var visibility = (await policy.ResolveAsync(userId, [senderId], token))[senderId];

        // 번역 이벤트는 별도 원문 우회 통로가 될 수 있으므로 차단 중에는 전송하지 않는다.
        if (type != "chat.message.created") return visibility.Blocked ? null : json;
        var dto = value["message"]!.AsObject();
        dto["contentVisibility"] = visibility.Blocked ? "BLOCKED" : "VISIBLE";
        dto["authorDeleted"] = visibility.AuthorDeleted;
        if (visibility.Blocked || visibility.AuthorDeleted)
        {
            dto["senderUserId"] = null;
            dto["senderEmail"] = null;
            dto["senderProfileImageUrl"] = null;
            dto["senderName"] = visibility.AuthorDeleted ? "탈퇴한 사용자" : null;
            if (visibility.Blocked)
            {
                dto["content"] = "차단한 사용자의 메시지입니다.";
                dto["translations"] = new JsonArray();
                dto["sender"] = null;
                dto["unreadMemberCount"] = null;
            }
            else if (dto["sender"] is JsonObject open)
            {
                open["nickname"] = "탈퇴한 사용자";
                open["profileImageUrl"] = null;
            }
        }
        return value.ToJsonString();
    }

    public async Task<string?> PreparePrivateAsync(long userId, string json, CancellationToken token)
    {
        var value = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Invalid CHAT private event.");
        if (value["eventType"]?.GetValue<string>() != "chat.notification.created") return json;
        long? id = value["notification"]?["id"]?.GetValue<long>();
        if (id is null) return null;
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await db.ChatNotifications.AsNoTracking().Where(row => row.Id == id && row.RecipientUserId == userId)
            .Select(row => new { row.DeletedAt, row.ActorUserId }).SingleOrDefaultAsync(token);
        if (row is null || row.DeletedAt is not null || row.ActorUserId is { } actor
            && await db.ChatAccountDeletions.AnyAsync(gone => gone.UserId == actor, token)) return null;
        return json;
    }
}
