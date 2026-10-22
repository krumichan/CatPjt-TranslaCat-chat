using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TranslaCat.Chat.Api.Push;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Infrastructure.Push;

namespace TranslaCat.Chat.ApiTests.Push;

public sealed class OpenInvitationPushContractTests
{
    [Fact]
    public void Large_ids_are_exact_decimal_strings_and_old_message_payload_remains_byte_compatible()
    {
        // 준비
        var target = new ChatCorePushTarget(22, "c2a1b179-bbe5-4be9-a8c9-6206767d4d60", "synthetic_binding", 1, 1,
            "ExpoPushToken[synthetic-token]", "android");
        var invitation = new OpenInvitationPushOutboxEntity
        { NotificationId = long.MaxValue, RoomId = 9007199254740993, EventId = "event" };
        var message = new ChatPushOutboxEntity { MessageId = long.MaxValue, RoomId = 9007199254740993, EventId = "event" };

        // 실행
        string v1 = ExpoChatPushClient.SerializePayload(ChatPushProcessor.Payload(message, target));
        string v2 = ExpoChatPushClient.SerializePayload(OpenInvitationPushProcessor.Payload(invitation, target));
        using var payload = JsonDocument.Parse(v2);
        var data = payload.RootElement.GetProperty("data");

        // 검증: v1에는 kind/notificationId를 추가하지 않고 기존 JSON 전체를 그대로 보존한다.
        Assert.Equal("9223372036854775807", data.GetProperty("notificationId").GetString());
        Assert.Equal("9007199254740993", data.GetProperty("roomId").GetString());
        Assert.Equal(6, data.EnumerateObject().Count());
        Assert.False(data.TryGetProperty("messageId", out _));
        Assert.Equal("{\"to\":\"ExpoPushToken[synthetic-token]\",\"title\":\"TranslaCat\",\"body\":\"\\uC0C8 \\uCC44\\uD305 \\uBA54\\uC2DC\\uC9C0\\uAC00 \\uC788\\uC5B4\\uC694.\",\"channelId\":\"chat-messages\",\"sound\":\"default\",\"data\":{\"schemaVersion\":1,\"eventId\":\"event\",\"roomId\":\"9007199254740993\",\"messageId\":\"9223372036854775807\",\"accountBindingId\":\"synthetic_binding\"}}", v1);
    }

    [Fact]
    public void Invitation_worker_follows_existing_explicit_push_flag_and_additive_DI_registration()
    {
        // 준비
        var disabled = new ServiceCollection();
        var enabled = new ServiceCollection();
        var values = new Dictionary<string, string?>
        {
            ["Chat:Push:Enabled"] = "true", ["Chat:Push:AppEnvironment"] = "development",
            ["Chat:Push:ProjectId"] = "55b79b33-07b9-4d4b-a3f8-cf24ea76b139",
            ["Chat:Database:ConnectionString"] = "synthetic", ["Chat:Core:Enabled"] = "true"
        };

        // 실행
        disabled.AddChatPush(new ConfigurationBuilder().Build());
        enabled.AddChatPush(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        // 검증
        Assert.DoesNotContain(disabled, row => row.ImplementationType == typeof(OpenInvitationPushWorker));
        Assert.Contains(enabled, row => row.ImplementationType == typeof(ChatPushWorker));
        Assert.Contains(enabled, row => row.ImplementationType == typeof(OpenInvitationPushWorker));
        Assert.Contains(enabled, row => row.ServiceType == typeof(IOpenInvitationPushStore) && row.ImplementationType == typeof(EfOpenInvitationPushStore));
    }
}
