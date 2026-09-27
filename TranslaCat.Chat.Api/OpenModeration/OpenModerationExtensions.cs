using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TranslaCat.Chat.Api.Read;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Api.Runtime;
using TranslaCat.Chat.Application.Membership;
using TranslaCat.Chat.Application.Notifications;
using TranslaCat.Chat.Application.OpenMembership;
using TranslaCat.Chat.Application.OpenModeration;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.OpenModeration;

namespace TranslaCat.Chat.Api.OpenModeration;

public static class OpenModerationExtensions
{
    public static IServiceCollection AddOpenModerationHttp(this IServiceCollection services)
    {
        services.TryAddSingleton<OpenModerationMapper>();
        services.TryAddScoped(provider => new OpenModerationService(provider.GetService<IOpenModerationStore>()
            ?? throw new OpenRoomDependencyUnavailableException(),
            () => provider.GetRequiredService<ChatReadLocalTime>().FromUtc(provider.GetRequiredService<TimeProvider>().GetUtcNow())));
        return services;
    }

    public static IServiceCollection AddOpenModerationPersistence(this IServiceCollection services)
    {
        services.TryAddScoped<IOpenBanEventDelivery, OpenBanRealtimeDelivery>();
        services.TryAddScoped<IOpenModerationStore>(provider => new EfOpenModerationStore(
            provider.GetRequiredService<IDbContextFactory<ChatDbContext>>(), provider.GetRequiredService<ILogger<EfOpenModerationStore>>(),
            provider.GetService<IChatRoomAccountReader>(), provider.GetService<IChatMembershipDirectory>(),
            provider.GetService<IOpenProfileStorage>(), provider.GetService<IOpenMembershipDelivery>(), provider.GetService<IOpenBanEventDelivery>()));
        return services;
    }
}

public sealed class OpenBanRealtimeDelivery(IServiceProvider services, ChatReadLocalTime localTime) : IOpenBanEventDelivery
{
    private readonly ChatReadTimestampFormatter timestamps = new(localTime.SourceTimeZone);

    public async Task ValidateAvailabilityAsync(CancellationToken token)
    {
        _ = Relay();
        _ = Notifications();
        await (services.GetService<IChatActivityNotificationDelivery>() ?? throw new OpenRoomDependencyUnavailableException())
            .ValidateAvailabilityAsync(token);
    }

    public async Task DeliverAsync(OpenMemberBanned change, CancellationToken token)
    {
        // 방 이벤트와 대상 private queue를 구분한다. ban된 사용자의 새 방 구독은 허용하지 않는다.
        var payload = JsonSerializer.Serialize(new
        {
            eventType = "chat.member.banned",
            roomId = change.RoomId,
            targetOpenChatMemberId = change.MemberId,
            reason = change.Reason,
            bannedAt = timestamps.Format(change.BannedAt),
            occurredAt = timestamps.Format(change.OccurredAt)
        });
        await Relay().PublishRoomAsync(change.RoomId, payload, token);
        await Relay().PublishUserAsync(change.Username, "/queue/chat/open-rooms/" + change.RoomId.ToString(CultureInfo.InvariantCulture), payload, token);

        // source commit 뒤 별도 transaction의 실제 활동 알림 writer가 source key 중복을 제어한다.
        await Notifications().CreateAsync(new(change.UserId, change.Username, "OPEN_CHAT_KICKED", change.RoomId,
            change.ActorUserId, JsonSerializer.Serialize(new
            {
                roomName = change.RoomName,
                reason = change.Reason,
                bannedAt = change.BannedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.')
            }), "open-ban:" + change.BanId.ToString(CultureInfo.InvariantCulture), change.AuditIdentity), token);
    }

    private ChatRealtimeRedisRelay Relay()
    {
        return services.GetService<ChatRealtimeRedisRelay>() ?? throw new OpenRoomDependencyUnavailableException();
    }

    private IChatActivityNotificationWriter Notifications()
    {
        return services.GetService<IChatActivityNotificationWriter>() ?? throw new OpenRoomDependencyUnavailableException();
    }
}
