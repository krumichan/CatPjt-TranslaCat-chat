using System.Globalization;
using System.Text.Json;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Api.Runtime;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Application.OpenMembership;
using TranslaCat.Chat.Application.OpenRooms;

namespace TranslaCat.Chat.Api.OpenMembership;

public sealed class OpenMembershipRealtimeDelivery(IServiceProvider services, ChatReadLocalTime localTime) : IOpenMembershipDelivery
{
    private readonly ChatReadTimestampFormatter timestamps = new(localTime.SourceTimeZone);

    public async Task ValidateAvailabilityAsync(IReadOnlyList<OpenMembershipIntent> intents, CancellationToken token)
    {
        if (intents.Count == 0)
        {
            return;
        }

        _ = Relay();
        if (intents.OfType<OpenMemberProfileChanged>().Any())
        {
            _ = Profiles();
        }

        bool hasNotification = intents.OfType<OpenMemberRoleChanged>().Any(row => row.UserId != row.ActorUserId)
            || intents.OfType<OpenRoomClosed>().Any(row => row.RecipientUserIds.Count > 0);
        if (hasNotification)
        {
            // 실제 수신자 이메일은 계정 소유 서비스에서만 구한다. 고정 사용자/이메일 대역은 운영에 없다.
            _ = Directory();
            _ = Notifications();
            await (services.GetService<IChatActivityNotificationDelivery>() ?? throw new OpenRoomDependencyUnavailableException())
                .ValidateAvailabilityAsync(token);
        }
    }

    public async Task DeliverAsync(OpenMembershipIntent intent, CancellationToken token)
    {
        switch (intent)
        {
            case OpenMembersChanged changed:
                await Relay().PublishRoomAsync(changed.RoomId, JsonSerializer.Serialize(new
                {
                    eventType = "chat.members.changed",
                    roomId = changed.RoomId,
                    occurredAt = timestamps.Format(changed.OccurredAt)
                }), token);
                break;
            case OpenMemberProfileChanged profile:
                await Profiles().DeliverAsync(profile.Profile, token);
                break;
            case OpenMemberRoleChanged role:
                await Relay().PublishRoomAsync(role.RoomId, JsonSerializer.Serialize(new
                {
                    eventType = "chat.member.role.updated",
                    roomId = role.RoomId,
                    targetOpenChatMemberId = role.MemberId,
                    role = role.Role,
                    occurredAt = timestamps.Format(role.OccurredAt)
                }), token);
                if (role.UserId != role.ActorUserId)
                {
                    await NotifyAsync(role.UserId, "OPEN_CHAT_ROLE_CHANGED", role.RoomId, role.ActorUserId,
                        JsonSerializer.Serialize(new
                        {
                            roomName = role.RoomName,
                            newRole = role.Role
                        }),
                        $"open-role:{role.RoomId}:{role.MemberId}:{role.Role}:{JavaLocalTime(role.OccurredAt)}", role.AuditIdentity, token);
                }
                break;
            case OpenRoomClosed closed:
                // 종료 뒤 일반 권한 검사가 막는 기존 구독에도 종료 이벤트만 전달하고 구독을 제거한다.
                await Relay().PublishRoomClosureAsync(closed.RoomId, JsonSerializer.Serialize(new
                {
                    eventType = "chat.room.closed",
                    roomId = closed.RoomId,
                    closedAt = timestamps.Format(closed.ClosedAt),
                    occurredAt = timestamps.Format(closed.OccurredAt)
                }), token);
                foreach (long recipient in closed.RecipientUserIds)
                {
                    await NotifyAsync(recipient, "OPEN_CHAT_ROOM_CLOSED", closed.RoomId, closed.ActorUserId,
                        JsonSerializer.Serialize(new
                        {
                            roomName = closed.RoomName,
                            closedAt = LocalJsonTime(closed.ClosedAt)
                        }),
                        $"open-close:{closed.RoomId}:{JavaLocalTime(closed.ClosedAt)}", closed.AuditIdentity, token);
                }
                break;
            default:
                throw new InvalidOperationException("Unknown OPEN membership event.");
        }
    }

    private async Task NotifyAsync(long userId, string type, long roomId, long actor, string payload, string key, string audit, CancellationToken token)
    {
        var recipient = await Directory().FindByIdAsync(userId, token);
        if (recipient is null)
        {
            return;
        }

        await Notifications().CreateAsync(new(userId, recipient.Email, type, roomId, actor, payload, key, audit), token);
    }

    private static string JavaLocalTime(DateTime value)
    {
        return value.Second == 0 && value.Ticks % TimeSpan.TicksPerSecond == 0
        ? value.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture)
        : new ChatReadTimestampFormatter(TimeZoneInfo.Utc).Format(value)[..^1];
    }

    // 알림 payload는 @ChatUtcTimestamp가 없는 LocalDateTime이다. 공개 room event의 UTC 문자열과 구분한다.
    private static string LocalJsonTime(DateTime value)
    {
        return value.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.');
    }

    private ChatRealtimeRedisRelay Relay()
    {
        return services.GetService<ChatRealtimeRedisRelay>() ?? throw new OpenRoomDependencyUnavailableException();
    }

    private IOpenProfileEventDelivery Profiles()
    {
        return services.GetService<IOpenProfileEventDelivery>() ?? throw new OpenRoomDependencyUnavailableException();
    }

    private IChatMembershipDirectory Directory()
    {
        return services.GetService<IChatMembershipDirectory>() ?? throw new OpenRoomDependencyUnavailableException();
    }

    private IChatActivityNotificationWriter Notifications()
    {
        return services.GetService<IChatActivityNotificationWriter>() ?? throw new OpenRoomDependencyUnavailableException();
    }
}
