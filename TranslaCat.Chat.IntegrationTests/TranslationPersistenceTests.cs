using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Translation;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Infrastructure.Translation;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class TranslationPersistenceTests(ChatMySqlFixture fixture)
{
    private static readonly DateTime Now = ChatMySqlFixture.Epoch.AddMinutes(10);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(17);

    [Fact]
    public async Task Concurrent_workers_only_one_claims_pending_translation()
    {
        // 준비
        var seeded = await SeedAsync();

        // 실행 — 각 store 호출은 별도 실제 MySQL connection/transaction을 사용한다.
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Store().TryClaimAsync(seeded.Id, seeded.Room.FirstId, false, Lease, default)));

        // 검증
        var claim = Assert.Single(claims, value => value is not null);
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        var row = await context.ChatMessageTranslations.SingleAsync(value => value.Id == seeded.Id);
        Assert.Equal(claim!.Token, row.ProcessingToken);
        Assert.NotNull(row.ProcessingExpiresAt);
        Assert.Equal("PENDING", row.Status);
    }

    [Fact]
    public async Task Active_lease_excludes_scan_and_cannot_be_claimed_again()
    {
        // 준비
        var seeded = await SeedAsync();
        var store = Store();
        Assert.NotNull(await store.TryClaimAsync(seeded.Id, null, false, Lease, default));

        // 실행
        var second = await store.TryClaimAsync(seeded.Id, null, true, Lease, default);
        var pending = await store.FindCandidatesAsync("PENDING", 100, default);

        // 검증
        Assert.Null(second);
        Assert.DoesNotContain(seeded.Id, pending);
    }

    [Fact]
    public async Task Expired_lease_can_be_reclaimed_and_late_owner_cannot_overwrite()
    {
        // 준비
        var seeded = await SeedAsync();
        var store = Store();
        var first = (await store.TryClaimAsync(seeded.Id, null, false, Lease, default))!;
        await ExpireAsync(seeded.Id);

        // 실행
        var second = (await store.TryClaimAsync(seeded.Id, null, false, Lease, default))!;
        var oldResult = await store.TryFinishAsync(first, "late stale", null, Now, default);
        var currentResult = await store.TryFinishAsync(second, "current synthetic", null, Now, default);

        // 검증
        Assert.NotEqual(first.Token, second.Token);
        Assert.Null(oldResult);
        Assert.Equal("COMPLETED", currentResult!.Status);
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        var row = await context.ChatMessageTranslations.SingleAsync(value => value.Id == seeded.Id);
        Assert.Equal("current synthetic", row.TranslatedContent);
        Assert.Null(row.ProcessingToken);
        Assert.Null(row.ProcessingExpiresAt);
        Assert.Equal(Now, row.CompletedAt);
    }

    [Fact]
    public async Task Expired_owner_is_rejected_even_before_another_worker_claims()
    {
        // 준비
        var seeded = await SeedAsync();
        var store = Store();
        var claim = (await store.TryClaimAsync(seeded.Id, null, false, Lease, default))!;
        await ExpireAsync(seeded.Id);

        // 실행
        var result = await store.TryFinishAsync(claim, "too late", null, Now, default);

        // 검증
        Assert.Null(result);
        Assert.Contains(seeded.Id, await store.FindCandidatesAsync("PENDING", 100, default));
    }

    [Fact]
    public async Task Wrong_message_deleted_and_completed_rows_never_claim()
    {
        // 준비
        var pending = await SeedAsync();
        var completed = await SeedAsync("COMPLETED");
        var deleted = await SeedAsync();
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            var row = await context.ChatMessageTranslations.SingleAsync(value => value.Id == deleted.Id);
            row.DeletedAt = Now;
            await context.SaveChangesAsync();
        }

        // 실행
        var results = await Task.WhenAll(
            Store().TryClaimAsync(pending.Id, pending.Room.SecondId, false, Lease, default),
            Store().TryClaimAsync(completed.Id, null, true, Lease, default),
            Store().TryClaimAsync(deleted.Id, null, true, Lease, default));

        // 검증
        Assert.All(results, Assert.Null);
    }

    [Fact]
    public async Task Failed_retry_preserves_old_content_and_failure_result_clears_completion_only()
    {
        // 준비
        var seeded = await SeedAsync("FAILED", "old synthetic");
        var store = Store();
        Assert.Null(await store.TryClaimAsync(seeded.Id, null, false, Lease, default));

        // 실행
        var claim = (await store.TryClaimAsync(seeded.Id, null, true, Lease, default))!;
        var change = await store.TryFinishAsync(claim, null, "safe failure", Now, default);

        // 검증
        Assert.Equal("FAILED", change!.Status);
        Assert.Null(change.TranslatedContent);
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        var row = await context.ChatMessageTranslations.SingleAsync(value => value.Id == seeded.Id);
        Assert.Equal("old synthetic", row.TranslatedContent);
        Assert.Equal("safe failure", row.FailureReason);
        Assert.Null(row.CompletedAt);
        Assert.Null(row.ProcessingToken);
    }

    [Fact]
    public async Task Deferred_failure_blocks_automatic_reclaim_until_provider_wait_has_elapsed()
    {
        // 준비: 이 테스트 DB의 한 번역만 claim한다.
        var seeded = await SeedAsync();
        var store = Store();
        var claim = (await store.TryClaimAsync(seeded.Id, null, false, Lease, default))!;

        // 실행: Provider가 지시한 600초를 기존 FAILED scan의 만료 조건에 기록한다.
        var change = await store.TryFinishAsync(
            claim, null, "AI Server Chat Translation Error", Now, default, TimeSpan.FromSeconds(600));
        var earlyCandidates = await store.FindCandidatesAsync("FAILED", 100, default);
        var earlyClaim = await store.TryClaimAsync(seeded.Id, null, true, Lease, default);

        // 검증: 아직 자동 호출은 없고, DB 시계가 지난 뒤에만 기존 retry가 가능하다.
        Assert.Equal("FAILED", change!.Status);
        Assert.DoesNotContain(seeded.Id, earlyCandidates);
        Assert.Null(earlyClaim);
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            var row = await context.ChatMessageTranslations.SingleAsync(value => value.Id == seeded.Id);
            Assert.NotNull(row.ProcessingExpiresAt);
            Assert.Null(row.ProcessingToken);
        }

        await ExpireAsync(seeded.Id);
        Assert.Contains(seeded.Id, await store.FindCandidatesAsync("FAILED", 100, default));
        Assert.NotNull(await store.TryClaimAsync(seeded.Id, null, true, Lease, default));
    }

    [Theory]
    [InlineData("PENDING")]
    [InlineData("COMPLETED")]
    public async Task Manual_noop_checks_access_but_does_not_require_provider_or_audit(string status)
    {
        // 준비
        var seeded = await SeedAsync(status, "preserved");
        var store = new EfChatTranslationStore(fixture.Contexts, () => Now);

        // 실행
        var change = await store.RetryAsync(seeded.Room.UserId, seeded.Room.RoomId, seeded.Room.FirstId,
            "ja", false, Now, default);
        await Assert.ThrowsAsync<ChatMessageException>(() => store.RetryAsync(seeded.Room.UserId + 99,
            seeded.Room.RoomId, seeded.Room.FirstId, "ja", false, Now, default));

        // 검증
        Assert.Equal(status, change.Translation.Status);
        Assert.Equal("preserved", change.Translation.TranslatedContent);
        Assert.Null(change.Intent);
    }

    [Fact]
    public async Task Manual_failed_retry_commits_pending_before_dispatch_and_normalizes_language()
    {
        // 준비
        var seeded = await SeedAsync("FAILED", "retained");
        var dispatcher = new ObservingDispatcher(fixture, seeded.Id);
        var service = new ChatTranslationRetryService(Store(), dispatcher, () => Now);

        // 실행
        var response = await service.RetryAsync(seeded.Room.UserId, seeded.Room.RoomId, seeded.Room.FirstId, " JA ");

        // 검증
        Assert.Equal("PENDING", response.Status);
        Assert.Equal("retained", response.TranslatedContent);
        Assert.Null(response.FailureReason);
        Assert.Null(response.CompletedAt);
        Assert.True(dispatcher.ObservedCommittedPending);
        Assert.Equal([seeded.Id], Assert.Single(dispatcher.Events).TranslationIds);
    }

    [Fact]
    public async Task Unconfigured_failed_retry_rolls_back_and_no_event_is_dispatched()
    {
        // 준비
        var seeded = await SeedAsync("FAILED");
        var dispatcher = new ObservingDispatcher(fixture, seeded.Id) { IsConfigured = false };
        var service = new ChatTranslationRetryService(Store(), dispatcher, () => Now);

        // 실행
        await Assert.ThrowsAsync<ChatMessageDependencyUnavailableException>(() => service.RetryAsync(
            seeded.Room.UserId, seeded.Room.RoomId, seeded.Room.FirstId, "ja"));

        // 검증
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal("FAILED", (await context.ChatMessageTranslations.SingleAsync(value => value.Id == seeded.Id)).Status);
        Assert.Empty(dispatcher.Events);
    }

    [Fact]
    public async Task Processor_commits_result_before_delivery_and_recovers_orphan_pending()
    {
        // 준비 — client/event는 명시적 합성 대역이며 claim/결과 저장은 실제 MySQL이다.
        var seeded = await SeedAsync();
        var client = new SyntheticClient();
        var delivery = new ObservingDelivery(fixture);
        var processor = new ChatTranslationProcessor(Store(), client, delivery, new(), () => Now, (_, _) => { });

        // 실행 — 큐 통지를 전혀 보내지 않고 DB 행만으로 실행한다.
        var result = await processor.ProcessOneAsync(seeded.Id, null, false, default);

        // 검증
        Assert.Equal(ChatTranslationProcessResult.Completed, result);
        Assert.Equal(1, client.CallCount);
        Assert.True(delivery.ObservedCommitted);
        Assert.Equal(seeded.Id, Assert.Single(delivery.Events).TranslationId);
    }

    [Fact]
    public async Task Open_ban_is_checked_before_missing_target_on_manual_retry()
    {
        // 준비
        var seeded = await SeedAsync(roomType: "OPEN");
        await using (var context = await fixture.Contexts.CreateDbContextAsync())
        {
            context.OpenChatRooms.Add(new OpenChatRoomEntity
            {
                ChatRoomId = seeded.Room.RoomId,
                MaxMemberCount = 100,
                Status = "ACTIVE",
                Visibility = "PUBLIC",
                CreatedAt = Now,
                UpdatedAt = Now
            });
            context.OpenChatBans.Add(new OpenChatBanEntity
            {
                ChatRoomId = seeded.Room.RoomId,
                TargetUserId = seeded.Room.UserId,
                TargetChatRoomMemberId = seeded.Room.MemberId,
                TargetMemberCode = "synthetic-member",
                NicknameSnapshot = "synthetic",
                TargetRoleSnapshot = "MEMBER",
                LastJoinedAtSnapshot = Now,
                BannedByMemberId = seeded.Room.MemberId,
                BannedByRole = "OWNER",
                BannedAt = Now,
                Reason = "synthetic reason",
                CreatedAt = Now,
                UpdatedAt = Now
            });
            await context.SaveChangesAsync();
        }

        // 실행
        var exception = await Assert.ThrowsAsync<ChatMessageException>(() => Store().RetryAsync(
            seeded.Room.UserId, seeded.Room.RoomId, long.MaxValue, "ja", true, Now, default));

        // 검증
        Assert.Equal("OPEN_CHAT_BANNED", exception.ErrorCode);
    }

    private EfChatTranslationStore Store()
    {
        return new(fixture.Contexts, () => Now, new SyntheticProfiles());
    }

    private async Task<(SeededReadRoom Room, long Id)> SeedAsync(
        string status = "PENDING", string? content = null, string roomType = "GROUP")
    {
        var room = await fixture.SeedReadRoomAsync(roomType);
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        var translation = new ChatMessageTranslationEntity
        {
            ChatMessageId = room.FirstId,
            LanguageCode = "ja",
            Status = status,
            TranslatedContent = content,
            FailureReason = status == "FAILED" ? "previous failure" : null,
            CompletedAt = status == "COMPLETED" ? ChatMySqlFixture.Epoch : null,
            CreatedAt = ChatMySqlFixture.Epoch,
            UpdatedAt = ChatMySqlFixture.Epoch
        };
        context.ChatMessageTranslations.Add(translation);
        await context.SaveChangesAsync();
        return (room, translation.Id);
    }

    private async Task ExpireAsync(long id)
    {
        await using var context = await fixture.Contexts.CreateDbContextAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_message_translation SET processing_expires_at = UTC_TIMESTAMP(6) - INTERVAL 1 SECOND WHERE id = {id}");
    }

    private sealed class SyntheticProfiles : IChatMessageProfileReader
    {
        public Task<string> GetAuditIdentityAsync(long userId, CancellationToken cancellationToken)
        {
            return Task.FromResult("synthetic-audit");
        }

        public Task<ChatUserMessageProfile> GetUserAsync(long userId, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<string?> ResolveObjectUrlAsync(string objectKey, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class SyntheticClient : IChatTranslationClient
    {
        public bool IsConfigured => true;
        public int CallCount
        {
            get; private set;
        }
        public Task<string> TranslateAsync(string text, string targetLanguageCode, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult("合成翻訳");
        }
    }

    private sealed class ObservingDispatcher(ChatMySqlFixture fixture, long id) : IChatTranslationDispatcher
    {
        public bool IsConfigured { get; init; } = true;
        public bool ObservedCommittedPending
        {
            get; private set;
        }
        public List<ChatTranslationRequestedIntent> Events { get; } = [];
        public async Task DispatchAsync(ChatTranslationRequestedIntent intent, CancellationToken cancellationToken)
        {
            await using var context = await fixture.Contexts.CreateDbContextAsync(cancellationToken);
            ObservedCommittedPending = await context.ChatMessageTranslations.AnyAsync(row => row.Id == id && row.Status == "PENDING", cancellationToken);
            Events.Add(intent);
        }
    }

    private sealed class ObservingDelivery(ChatMySqlFixture fixture) : IChatTranslationEventDelivery
    {
        public bool ObservedCommitted
        {
            get; private set;
        }
        public List<ChatTranslationChanged> Events { get; } = [];
        public async Task DeliverAsync(ChatTranslationChanged change, CancellationToken cancellationToken)
        {
            await using var context = await fixture.Contexts.CreateDbContextAsync(cancellationToken);
            ObservedCommitted = await context.ChatMessageTranslations.AnyAsync(row => row.Id == change.TranslationId
                && row.Status == change.Status && row.ProcessingToken == null, cancellationToken);
            Events.Add(change);
        }
    }
}
