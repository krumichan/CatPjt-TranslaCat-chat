using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TranslaCat.Chat.Application.Membership;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class MembershipRuntimeTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task HTTP_invitation_commits_then_delivers_SYSTEM_member_change_and_private_activity_across_apps()
    {
        // 준비: 계정·친구 port만 합성이고 JWT/HTTP/EF/MySQL/Redis/STOMP는 실제 구성이다.
        var room = await fixture.SeedReadRoomAsync(userId: Random.Shared.NextInt64(7_000_000_000, 8_000_000_000));
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            (await context.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId)).Role = "OWNER";
            (await context.ChatRooms.SingleAsync(row => row.Id == room.RoomId)).Name = "합성 초대방";
            await context.SaveChangesAsync();
        }
        var prefix = "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID") + ":membership:" + Guid.NewGuid().ToString("N");
        await using var sender = await ChatRuntimeTestHost.StartAsync(fixture, prefix,
            configure: services => services.AddSingleton<IChatMembershipDirectory>(new RuntimeDirectory(room.UserId, room.UserId + 1)));
        await using var receiver = await ChatRuntimeTestHost.StartAsync(fixture, prefix);
        using var ownerSocket = await receiver.ConnectAsync(room.UserId);
        using var invitedSocket = await receiver.ConnectAsync(room.UserId + 1);
        await ownerSocket.SubscribeAsync("room", $"/topic/chat/rooms/{room.RoomId}");
        await invitedSocket.SubscribeAsync("activities", "/user/queue/chat/notifications");

        // 실행
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/chat/rooms/{room.RoomId}/members/invitations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sender.CreateToken(room.UserId));
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            targetUserIds = new[] { room.UserId + 1 }
        }), Encoding.UTF8, "application/json");
        using var response = await sender.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var system = await ownerSocket.ReceiveEventAsync("chat.message.created");
        var changed = await ownerSocket.ReceiveEventAsync("chat.members.changed");
        var activity = await invitedSocket.ReceiveEventAsync("chat.notification.created");

        // 검증: wire 수신 시 독립 DB connection에서도 committed 상태가 보여야 한다.
        Assert.Equal("SYSTEM", system.GetProperty("message").GetProperty("messageType").GetString());
        Assert.Equal(room.RoomId, changed.GetProperty("roomId").GetInt64());
        var notification = activity.GetProperty("notification");
        Assert.Equal("CHAT_INVITATION", notification.GetProperty("notificationType").GetString());
        Assert.Equal("합성 초대방", notification.GetProperty("payload").GetProperty("roomName").GetString());
        Assert.False(notification.GetProperty("isRead").GetBoolean());
        await using var verification = await fixture.Contexts.CreateDbContextAsync();
        var savedMember = await verification.ChatRoomMembers.SingleAsync(row => row.ChatRoomId == room.RoomId && row.UserId == room.UserId + 1);
        Assert.Equal(room.SecondId, savedMember.LastReadMessageId);
        Assert.True(savedMember.Active);
        Assert.True(await verification.ChatNotifications.AnyAsync(row => row.Id == notification.GetProperty("id").GetInt64()
            && row.RecipientUserId == room.UserId + 1));
        Assert.True(await verification.ChatMessages.AnyAsync(row => row.Id == system.GetProperty("message").GetProperty("id").GetInt64()));
    }

    [Fact]
    public async Task Missing_shared_directory_fails_HTTP_closed_and_preserves_database()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync(userId: Random.Shared.NextInt64(7_000_000_000, 8_000_000_000));
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            (await context.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId)).Role = "OWNER";
            await context.SaveChangesAsync();
        }
        var prefix = "translacat:chat:test:" + Environment.GetEnvironmentVariable("CHAT_TEST_RUN_ID") + ":membership-gate:" + Guid.NewGuid().ToString("N");
        await using var host = await ChatRuntimeTestHost.StartAsync(fixture, prefix);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/chat/rooms/{room.RoomId}/members/invitations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.CreateToken(room.UserId));
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            targetUserIds = new[] { room.UserId + 1 }
        }), Encoding.UTF8, "application/json");

        // 실행
        using var response = await host.Client.SendAsync(request);

        // 검증
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var verification = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(1, await verification.ChatRoomMembers.CountAsync(row => row.ChatRoomId == room.RoomId));
        Assert.False(await verification.ChatNotifications.AnyAsync(row => row.ChatRoomId == room.RoomId));
        Assert.False(await verification.ChatMessages.AnyAsync(row => row.ChatRoomId == room.RoomId && row.MessageType == "SYSTEM"));
    }

    private sealed class RuntimeDirectory(long actor, long target) : IChatMembershipDirectory
    {
        public Task<ChatMembershipUser?> FindByIdAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult<ChatMembershipUser?>(userId == actor || userId == target
                        ? new(userId, $"synthetic-{userId}@example.invalid", "합성 계정", "public-" + userId, "합성 표시명", null) : null);
        }

        public Task<ChatMembershipUser?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken)
        {
            return Task.FromResult<ChatMembershipUser?>(null);
        }

        public Task<bool> AreFriendsAsync(long userId, long targetUserId, CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task<bool> IsBlockedBetweenAsync(long userId, long targetUserId, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }
    }
}
