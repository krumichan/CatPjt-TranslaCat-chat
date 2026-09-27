using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.OpenMembership;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence.AiManagement;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class ChatAiSystemSettingsConcurrencyTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task Two_PATCH_transactions_waiting_on_DEFAULT_both_commit_and_preserve_each_others_fields()
    {
        // 준비: 실제 EF store/각 독립 connection을 사용하며 계정·전달만 합성 대역이다.
        var store = Store();
        var original = await store.SystemSettingsAsync(901, null, ChatMySqlFixture.Epoch, default);
        await using var blocker = await fixture.Contexts.CreateDbContextAsync();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.ChatAiSystemSettings.FromSqlRaw(
            "SELECT * FROM chat_ai_system_setting WHERE id = 'DEFAULT' FOR UPDATE").ToListAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task<ChatAiSystemSettings>? first = null;
        Task<ChatAiSystemSettings>? second = null;
        bool released = false;

        try
        {
            // 실행: 동일 DEFAULT의 최초 INSERT/upsert 잠금에서 두 실제 PATCH를 함께 기다리게 한다.
            first = store.SystemSettingsAsync(901,
                new()
                {
                    ConversationCooldownSeconds = original.ConversationCooldownSeconds + 1
                },
                ChatMySqlFixture.Epoch.AddSeconds(1), deadline.Token);
            second = store.SystemSettingsAsync(902,
                new()
                {
                    MentionRateLimitCount = original.MentionRateLimitCount + 1
                },
                ChatMySqlFixture.Epoch.AddSeconds(2), deadline.Token);
            await using var observer = await fixture.Contexts.CreateDbContextAsync(deadline.Token);
            while (await observer.Database.SqlQueryRaw<int>("""
                SELECT COUNT(*) AS Value
                FROM performance_schema.data_lock_waits w
                JOIN performance_schema.data_locks l ON l.ENGINE_LOCK_ID = w.REQUESTING_ENGINE_LOCK_ID
                WHERE l.OBJECT_SCHEMA = DATABASE() AND l.OBJECT_NAME = 'chat_ai_system_setting'
                    AND l.INDEX_NAME = 'PRIMARY' AND l.LOCK_DATA LIKE '%DEFAULT%'
                """).SingleAsync(deadline.Token) < 2)
            {
                await Task.Delay(20, deadline.Token);
            }
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            await transaction.CommitAsync(deadline.Token);
            released = true;
            await Task.WhenAll(first, second);

            // 검증: 공유→배타 잠금 승격 deadlock 없이 두 PATCH가 현재 값을 병합해 저장한다.
            var result = await store.SystemSettingsAsync(901, null, ChatMySqlFixture.Epoch.AddSeconds(3), deadline.Token);
            Assert.Equal(original.ConversationCooldownSeconds + 1, result.ConversationCooldownSeconds);
            Assert.Equal(original.MentionRateLimitCount + 1, result.MentionRateLimitCount);
            Assert.Equal(original with
            {
                ConversationCooldownSeconds = original.ConversationCooldownSeconds + 1,
                MentionRateLimitCount = original.MentionRateLimitCount + 1
            }, result);
            Assert.Equal(1, await observer.ChatAiSystemSettings.CountAsync(row => row.Id == "DEFAULT", deadline.Token));
        }
        finally
        {
            // 실패해도 blocker와 pending 요청을 종료한 뒤 이번 합성 공통 설정 값만 원래대로 복원한다.
            deadline.Cancel();
            if (!released)
            {
                await transaction.RollbackAsync();
            }

            foreach (Task? task in new Task?[] { first, second })
            {
                if (task is null)
                {
                    continue;
                }

                try
                {
                    await task;
                }
                catch (Exception) when (task.IsFaulted || task.IsCanceled) { }
            }
            await RestoreAsync(original);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lookup_preserves_existing_values_and_only_repairs_the_legacy_zero_delay_pair(bool legacy)
    {
        // 준비: 기존 합성 DEFAULT를 snapshot하고 다른 유효 값 및 명시적 false를 설정한다.
        var store = Store();
        var original = await store.SystemSettingsAsync(901, null, ChatMySqlFixture.Epoch, default);
        var stored = original with
        {
            ConversationCooldownSeconds = 271,
            ResponseDelayEnabled = false,
            ResponseDelayMinMillis = legacy ? 0 : 321,
            ResponseDelayMaxMillis = legacy ? 0 : 987
        };
        await RestoreAsync(stored);
        try
        {
            // 실행
            var result = await store.SystemSettingsAsync(901, null, ChatMySqlFixture.Epoch.AddMinutes(1), default);

            // 검증: 정상 조회는 false/기존 값을 덮어쓰지 않으며 0/0 보정도 다른 값을 바꾸지 않는다.
            var expected = legacy ? stored with
            {
                ResponseDelayEnabled = true,
                ResponseDelayMinMillis = 1200,
                ResponseDelayMaxMillis = 3500
            } : stored;
            Assert.Equal(expected, result);
            await using var read = await fixture.Contexts.CreateDbContextAsync();
            Assert.Equal(expected, EfChatAiSystemSettings.Map(await read.ChatAiSystemSettings.SingleAsync(row => row.Id == "DEFAULT")));
        }
        finally
        {
            await RestoreAsync(original);
        }
    }

    private EfChatAiManagementStore Store()
    {
        return new(fixture.Contexts, new NoExternalEvents(),
        NullLogger<EfChatAiManagementStore>.Instance, new SyntheticAccounts());
    }

    private async Task RestoreAsync(ChatAiSystemSettings value)
    {
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        var row = await context.ChatAiSystemSettings.SingleAsync(row => row.Id == "DEFAULT");
        EfChatAiSystemSettings.Apply(row, value);
        await context.SaveChangesAsync();
    }

    private sealed class SyntheticAccounts : IChatRoomAccountReader
    {
        public Task EnsureUserExistsAsync(long? userId, CancellationToken token)
        {
            throw new InvalidOperationException("Unused test port.");
        }

        public Task<string> GetAuditIdentityAsync(long userId, CancellationToken token)
        {
            return Task.FromResult("synthetic-system-admin");
        }

        public Task<ChatDirectPartner> GetDirectPartnerAsync(long userId, CancellationToken token)
        {
            throw new InvalidOperationException("Unused test port.");
        }
    }

    private sealed class NoExternalEvents : IOpenMembershipDelivery
    {
        public Task ValidateAvailabilityAsync(IReadOnlyList<OpenMembershipIntent> intents, CancellationToken token)
        {
            Assert.Empty(intents);
            return Task.CompletedTask;
        }
        public Task DeliverAsync(OpenMembershipIntent intent, CancellationToken token)
        {
            throw new InvalidOperationException("System settings have no room event.");
        }
    }
}
