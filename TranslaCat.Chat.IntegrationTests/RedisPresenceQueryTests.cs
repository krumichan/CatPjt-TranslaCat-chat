using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Infrastructure.Persistence.Repositories;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class RedisPresenceQueryTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task FanoutQuery_UsesChatMembershipAndPrivateAiStateWithoutAccountJoin()
    {
        // 준비: migration을 적용한 실제 MySQL CHAT 전용 catalog에만 합성 row를 만든다.
        var userId = Random.Shared.NextInt64(1_000_000_000, long.MaxValue);
        var visible = await fixture.SeedReadRoomAsync("GROUP", userId);
        var hidden = await fixture.SeedReadRoomAsync("DIRECT", userId);
        var inactive = await fixture.SeedReadRoomAsync("OPEN", userId);
        var deleted = await fixture.SeedReadRoomAsync("GROUP", userId);
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            context.ChatRoomAiSettings.Add(new ChatRoomAiSettingEntity
            {
                ChatRoomId = hidden.RoomId,
                DisclosureType = "PRIVATE",
                MentionPermission = "ALL_MEMBERS",
                CreatedAt = ChatMySqlFixture.Epoch,
                UpdatedAt = ChatMySqlFixture.Epoch
            });
            (await context.ChatRoomMembers.SingleAsync(member => member.Id == inactive.MemberId)).Active = false;
            (await context.ChatRoomMembers.SingleAsync(member => member.Id == deleted.MemberId)).DeletedAt = ChatMySqlFixture.Epoch;
            await context.SaveChangesAsync();
        }

        // 실행
        var memberships = await new EfChatPresenceRoomReader(fixture.Contexts)
            .FindActiveMembershipsAsync(userId, CancellationToken.None);

        // 검증: PRIVATE 판정 정보는 남기고 제외 여부는 fan-out 정책이 결정한다.
        Assert.Equal(2, memberships.Count);
        var group = Assert.Single(memberships, member => member.RoomId == visible.RoomId);
        Assert.Equal(visible.MemberId, group.MemberId);
        Assert.Equal("GROUP", group.RoomType);
        Assert.False(group.PrivateAi);
        Assert.True(Assert.Single(memberships, member => member.RoomId == hidden.RoomId).PrivateAi);
        Assert.DoesNotContain(memberships, member => member.RoomId == inactive.RoomId || member.RoomId == deleted.RoomId);
    }
}
