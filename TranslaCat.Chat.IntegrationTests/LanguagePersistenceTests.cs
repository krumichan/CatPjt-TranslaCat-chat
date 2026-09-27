using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Language;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Infrastructure.Persistence.Language;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class LanguagePersistenceTests(ChatMySqlFixture database)
{
    [Fact]
    public async Task Legacy_language_stores_trimmed_case_and_reset_preserves_raw_flags_contract_and_cursor()
    {
        // 준비
        var room = await database.SeedReadRoomAsync(userId: NewUserId());
        var store = new EfChatLanguageStore(database.Contexts, new SyntheticLanguageProfiles(), () => ChatMySqlFixture.Epoch);
        var legacy = new ChatLegacyMemberLanguageService(store);
        await new ChatLanguageService(store).UpdateDefaultAsync(room.UserId, new("fr", "de", false, false));
        await using (var context = await database.Contexts.CreateDbContextAsync())
        {
            var member = await context.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId);
            member.LastReadMessageId = room.SecondId;
            member.LastReadAt = ChatMySqlFixture.Epoch;
            await context.SaveChangesAsync();
        }

        // 실행 / 검증: DB에는 trim만 적용하고 response resolver가 lower case를 계산한다.
        var changed = await legacy.UpdateAsync(room.UserId, room.RoomId, new(" EN ", " JA ", true, false));
        Assert.Equal("en", changed.Values.OriginalLanguageCode);
        await using (var context = await database.Contexts.CreateDbContextAsync())
        {
            var member = await context.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId);
            Assert.Equal("EN", member.OriginalLanguageCode);
            Assert.Equal("JA", member.TranslationLanguageCode);
        }

        // 실행 / 검증: reset은 코드만 개인 기본으로 돌리고 구형 응답의 플래그는 member true를 사용한다.
        var reset = await legacy.ResetAsync(room.UserId, room.RoomId);
        Assert.Equal(new ChatLanguageValues("fr", "de", true, true), reset.Values);
        Assert.False(reset.RoomLanguageSettingApplied);
        await using var verification = await database.Contexts.CreateDbContextAsync();
        var saved = await verification.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId);
        Assert.Null(saved.OriginalLanguageCode);
        Assert.Equal(room.SecondId, saved.LastReadMessageId);
        Assert.Equal(ChatMySqlFixture.Epoch, saved.LastReadAt);
    }

    [Fact]
    public async Task Defaults_GET_does_not_insert_and_partial_default_PATCH_resets_missing_values_to_system()
    {
        // 준비
        var room = await database.SeedReadRoomAsync(userId: NewUserId());
        var service = Service();

        // 실행 / 검증: GET의 SYSTEM fallback은 영속 행을 만들지 않는다.
        var initial = await service.GetDefaultAsync(room.UserId);
        Assert.Equal("SYSTEM", initial.Source);
        await using (var context = await database.Contexts.CreateDbContextAsync())
        {
            Assert.False(await context.UserChatLanguageSettings.AnyAsync(row => row.UserId == room.UserId));
        }

        // 실행 / 검증: 개인 기본값의 PATCH 누락은 이전 값 보존이 아니다.
        await service.UpdateDefaultAsync(room.UserId, new(" EN ", " FR ", false, false));
        var changed = await service.UpdateDefaultAsync(room.UserId, new(null, " DE ", null, null));
        Assert.Equal(new ChatLanguageValues("ko", "de", true, true), changed.Values);
        Assert.Equal("DEFAULT", changed.Source);
    }

    [Fact]
    public async Task Room_override_merges_effective_values_and_reset_restores_default_without_changing_cursor()
    {
        // 준비
        var room = await database.SeedReadRoomAsync(userId: NewUserId());
        var service = Service();
        await service.UpdateDefaultAsync(room.UserId, new("en", "fr", false, false));
        await using (var context = await database.Contexts.CreateDbContextAsync())
        {
            var member = await context.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId);
            member.LastReadMessageId = room.FirstId;
            member.LastReadAt = ChatMySqlFixture.Epoch;
            await context.SaveChangesAsync();
        }

        // 실행
        var first = await service.UpdateRoomAsync(room.UserId, room.RoomId, new(null, "JA", null, true));
        var second = await service.UpdateRoomAsync(room.UserId, room.RoomId, new(" KO ", null, null, null));
        var reset = await service.ResetRoomAsync(room.UserId, room.RoomId);

        // 검증
        Assert.Equal(new ChatLanguageValues("en", "ja", false, true), first.Values);
        Assert.Equal(new ChatLanguageValues("ko", "ja", false, true), second.Values);
        Assert.Equal("ROOM_OVERRIDE", second.Source);
        Assert.True(second.RoomLanguageSettingApplied);
        Assert.Equal(new ChatLanguageValues("en", "fr", false, false), reset.Values);
        Assert.False(reset.RoomLanguageSettingApplied);
        await using var verification = await database.Contexts.CreateDbContextAsync();
        var saved = await verification.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId);
        Assert.Null(saved.OriginalLanguageCode);
        Assert.Null(saved.TranslationLanguageCode);
        Assert.True(saved.ShowOriginal);
        Assert.True(saved.ShowTranslation);
        Assert.Equal(room.FirstId, saved.LastReadMessageId);
        Assert.Equal(ChatMySqlFixture.Epoch, saved.LastReadAt);
    }

    [Theory]
    [InlineData("other-user")]
    [InlineData("inactive")]
    [InlineData("deleted")]
    public async Task Room_access_rejects_nonmember_inactive_and_soft_deleted_members(string denied)
    {
        // 준비
        var room = await database.SeedReadRoomAsync(userId: NewUserId());
        await using (var context = await database.Contexts.CreateDbContextAsync())
        {
            var member = await context.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId);
            if (denied == "inactive")
            {
                member.Active = false;
            }
            if (denied == "deleted")
            {
                member.DeletedAt = ChatMySqlFixture.Epoch;
            }
            await context.SaveChangesAsync();
        }

        // 실행 / 검증
        var error = await Assert.ThrowsAsync<ChatLanguageException>(() => Service().GetRoomAsync(
            denied == "other-user" ? room.UserId + 1 : room.UserId, room.RoomId));
        Assert.Equal("CHAT_ROOM_ACCESS_DENIED", error.Code);
    }

    [Fact]
    public async Task Missing_shared_identity_adapter_does_not_insert_default_setting()
    {
        // 준비
        var room = await database.SeedReadRoomAsync(userId: NewUserId());
        var service = new ChatLanguageService(new EfChatLanguageStore(database.Contexts, null, () => ChatMySqlFixture.Epoch));

        // 실행 / 검증
        await Assert.ThrowsAsync<ChatLanguageDependencyUnavailableException>(() => service.UpdateDefaultAsync(room.UserId, new("en", "ja", true, true)));
        await using var context = await database.Contexts.CreateDbContextAsync();
        Assert.False(await context.UserChatLanguageSettings.AnyAsync(row => row.UserId == room.UserId));
    }

    private ChatLanguageService Service()
    {
        return new(new EfChatLanguageStore(database.Contexts, new SyntheticLanguageProfiles(), () => ChatMySqlFixture.Epoch));
    }

    private static long NewUserId()
    {
        return Random.Shared.NextInt64(10_000_000, 2_000_000_000);
    }

    private sealed class SyntheticLanguageProfiles : IChatMessageProfileReader
    {
        public Task<string> GetAuditIdentityAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult("synthetic-language@example.invalid");
        }

        public Task<ChatUserMessageProfile> GetUserAsync(long userId, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Not part of this test.");
        }

        public Task<string?> ResolveObjectUrlAsync(string objectKey, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Not part of this test.");
        }
    }
}
