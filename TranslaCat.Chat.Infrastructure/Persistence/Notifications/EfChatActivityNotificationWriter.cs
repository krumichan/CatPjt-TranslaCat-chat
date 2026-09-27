using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Notifications;

public sealed class EfChatActivityNotificationWriter(
    IDbContextFactory<ChatDbContext> contexts,
    IChatActivityNotificationDelivery? delivery,
    Func<DateTime> clock) : IChatActivityNotificationWriter
{
    public async Task<ChatNotificationActivity?> CreateAsync(ChatActivityNotificationRequest request, CancellationToken cancellationToken)
    {
        if (delivery is null)
        {
            throw new ChatNotificationDependencyUnavailableException();
        }
        if (request.NotificationType is not ("CHAT_INVITATION" or "OPEN_CHAT_KICKED" or "OPEN_CHAT_ROLE_CHANGED" or "OPEN_CHAT_ROOM_CLOSED")
            || string.IsNullOrWhiteSpace(request.RecipientEmail) || string.IsNullOrWhiteSpace(request.AuditIdentity)
            || string.IsNullOrEmpty(request.SourceEventKey) || request.SourceEventKey.Length > 160)
        {
            throw new ArgumentException("Invalid internal activity notification request.");
        }
        using var payload = JsonDocument.Parse(request.PayloadJson);
        if (payload.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Activity payload must be an object.");
        }

        // 원본 REQUIRES_NEW처럼 source transaction과 분리한다. 계정 이메일은 호출자가 공유 소유 서비스에서 확인해야 한다.
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        if (await context.ChatNotifications.AnyAsync(row => row.RecipientUserId == request.RecipientUserId
                && row.NotificationType == request.NotificationType && row.SourceEventKey == request.SourceEventKey, cancellationToken))
        {
            return null;
        }
        if (request.RoomId is { } roomId && !await context.ChatRooms.AnyAsync(room => room.Id == roomId, cancellationToken))
        {
            return null;
        }
        await delivery.ValidateAvailabilityAsync(cancellationToken);
        var entity = new ChatNotificationEntity
        {
            RecipientUserId = request.RecipientUserId,
            NotificationType = request.NotificationType,
            ChatRoomId = request.RoomId,
            ActorUserId = request.ActorUserId,
            PayloadJson = request.PayloadJson,
            SourceEventKey = request.SourceEventKey,
            CreatedBy = request.AuditIdentity,
            CreatedAt = clock()
        };
        context.ChatNotifications.Add(entity);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
        {
            // 사전 조회를 동시에 통과해도 DB unique key가 중복 알림과 중복 전달을 막는다.
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var activity = new ChatNotificationActivity(entity.Id, entity.NotificationType, entity.ChatRoomId,
            entity.PayloadJson, entity.IsRead, entity.ReadAt, entity.CreatedAt);
        await delivery.DeliverAsync(request.RecipientEmail, activity, clock(), cancellationToken);
        return activity;
    }
}
