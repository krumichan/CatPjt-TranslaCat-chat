using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TranslaCat.Chat.Api.Read.Contracts;
using TranslaCat.Chat.Api.Realtime;
using TranslaCat.Chat.Application.Presence;

namespace TranslaCat.Chat.Api.Presence;

public sealed class ChatPresenceFanout(
    IChatPresenceRoomReader rooms,
    ChatRealtimeBroker broker,
    ChatReadTimestampFormatter timestamps,
    ILogger<ChatPresenceFanout> logger,
    IChatPresenceProfileReader? profiles = null)
{
    public async Task HandleAsync(ChatPresenceChanged change, CancellationToken cancellationToken = default)
    {
        var memberships = await rooms.FindActiveMembershipsAsync(change.UserId, cancellationToken);
        string? publicId = null;
        var profileChecked = false;

        foreach (var membership in memberships)
        {
            // PRIVATE AI 방에서는 사람의 Presence도 숨긴다는 기존 정책을 보존한다.
            if (membership.PrivateAi)
            {
                continue;
            }

            string memberRef;
            if (membership.RoomType == "DIRECT")
            {
                if (!profileChecked)
                {
                    profileChecked = true;
                    publicId = await ResolvePublicIdAsync(change.UserId, cancellationToken);
                }

                if (string.IsNullOrWhiteSpace(publicId))
                {
                    continue;
                }
                memberRef = publicId;
            }
            else if (membership.RoomType is "GROUP" or "OPEN")
            {
                memberRef = membership.MemberId.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                continue;
            }

            // Redis subscriber마다 자신의 local socket에만 전달한다. Redis에 다시 publish하지 않는다.
            var payload = new ChatPresenceChangedDto("chat.presence.changed", membership.RoomId,
                membership.RoomType, memberRef, change.Online, timestamps.Format(change.OccurredAt));
            await broker.PublishRoomAsync(membership.RoomId, JsonSerializer.Serialize(payload), cancellationToken);
        }
    }

    private async Task<string?> ResolvePublicIdAsync(long userId, CancellationToken cancellationToken)
    {
        if (profiles is null)
        {
            logger.LogWarning("DIRECT presence fan-out is unavailable because the account public-ID adapter is not configured.");
            return null;
        }

        try
        {
            return await profiles.FindPublicIdAsync(userId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 계정 조회 장애가 다른 GROUP/OPEN 방의 독립적인 전달까지 막지 않게 한다.
            logger.LogWarning("DIRECT presence account lookup failed ({FailureType}).", exception.GetType().Name);
            return null;
        }
    }
}

public sealed record ChatPresenceChangedDto(
    [property: JsonPropertyName("eventType")] string EventType,
    [property: JsonPropertyName("roomId")] long RoomId,
    [property: JsonPropertyName("roomType")] string RoomType,
    [property: JsonPropertyName("memberRef")] string MemberRef,
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("occurredAt")] string OccurredAt);
