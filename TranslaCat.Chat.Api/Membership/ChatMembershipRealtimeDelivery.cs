using System.Globalization;
using System.Text.Json;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Api.Runtime;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Notifications;

namespace TranslaCat.Chat.Api.Membership;

public sealed class ChatMembershipRealtimeDelivery(IServiceProvider services, ChatReadLocalTime localTime) : IChatMembershipEventDelivery
{
    private readonly ChatReadTimestampFormatter timestamps = new(localTime.SourceTimeZone);

    public async Task ValidateAvailabilityAsync(IReadOnlyList<ChatMembershipIntent> intents, CancellationToken cancellationToken)
    {
        foreach (var intent in intents)
        {
            switch (intent)
            {
                case ChatMembershipMessageCreated created:
                    await Messages().ValidateAvailabilityAsync([new ChatMessageCreatedIntent(created.Message)], cancellationToken);
                    break;
                case ChatMembershipInvitationCommitted:
                    _ = Notifications();
                    var delivery = services.GetService<IChatActivityNotificationDelivery>() ?? throw new ChatMembershipDependencyUnavailableException();
                    await delivery.ValidateAvailabilityAsync(cancellationToken);
                    break;
                case ChatMembershipChanged:
                    _ = Relay();
                    break;
                default:
                    throw new InvalidOperationException("Unknown membership intent.");
            }
        }
    }

    public async Task DeliverAsync(ChatMembershipIntent intent, CancellationToken cancellationToken)
    {
        switch (intent)
        {
            case ChatMembershipMessageCreated created:
                await Messages().DeliverAsync(new ChatMessageCreatedIntent(created.Message), cancellationToken);
                break;
            case ChatMembershipChanged changed:
                await Relay().PublishRoomAsync(changed.RoomId, JsonSerializer.Serialize(new
                {
                    eventType = "chat.members.changed",
                    roomId = changed.RoomId,
                    occurredAt = timestamps.Format(changed.OccurredAt)
                }), cancellationToken);
                break;
            case ChatMembershipInvitationCommitted invitation:
                // source commit 뒤 별도 알림 transaction을 연다. source key는 Java LocalDateTime 표기를 유지한다.
                var sourceKey = "chat-invitation:" + invitation.RoomId.ToString(CultureInfo.InvariantCulture) + ":"
                    + invitation.MemberId.ToString(CultureInfo.InvariantCulture) + ":" + JavaLocalTime(invitation.JoinedAt);
                await Notifications().CreateAsync(new(invitation.RecipientUserId, invitation.RecipientEmail, "CHAT_INVITATION",
                    invitation.RoomId, invitation.ActorUserId, JsonSerializer.Serialize(new
                    {
                        roomName = invitation.RoomName
                    }),
                    sourceKey, invitation.AuditIdentity), cancellationToken);
                break;
            default:
                throw new InvalidOperationException("Unknown membership intent.");
        }
    }

    private static string JavaLocalTime(DateTime value)
    {
        if (value.Second == 0 && value.Ticks % TimeSpan.TicksPerSecond == 0)
        {
            return value.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
        }
        return new ChatReadTimestampFormatter(TimeZoneInfo.Utc).Format(value)[..^1];
    }

    private IChatMessageEventDelivery Messages()
    {
        return services.GetService<IChatMessageEventDelivery>() ?? throw new ChatMembershipDependencyUnavailableException();
    }

    private IChatActivityNotificationWriter Notifications()
    {
        return services.GetService<IChatActivityNotificationWriter>() ?? throw new ChatMembershipDependencyUnavailableException();
    }

    private ChatRealtimeRedisRelay Relay()
    {
        return services.GetService<ChatRealtimeRedisRelay>() ?? throw new ChatMembershipDependencyUnavailableException();
    }
}
