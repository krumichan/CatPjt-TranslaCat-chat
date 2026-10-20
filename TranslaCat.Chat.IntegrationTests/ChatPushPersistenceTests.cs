using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Infrastructure.Persistence.Repositories;
using TranslaCat.Chat.Infrastructure.Push;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class ChatPushPersistenceTests(ChatMySqlFixture fixture)
{
    [Theory]
    [InlineData("DIRECT")]
    [InlineData("GROUP")]
    [InlineData("OPEN")]
    public async Task Human_commit_enqueues_actual_joined_recipient_and_excludes_own_message(string type)
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync(type);
        long recipient = room.UserId + 1;
        await SeedRecipientAsync(room, recipient);
        if (type == "OPEN") await SeedOpenAsync(room);
        var transaction = Transaction();

        // 실행
        var created = await transaction.ExecuteAsync(async (session, token) =>
        {
            var member = await session.GetMemberAsync(room.UserId, room.RoomId, true, token);
            return await session.InsertTextAsync(member, "synthetic human", [], ChatMySqlFixture.Epoch.AddMinutes(1), token);
        }, default);

        // 검증: 독립 connection에서 본문과 outbox가 함께 보이고 OPEN도 private sender를 보존한다.
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        var job = await db.ChatPushOutbox.SingleAsync(row => row.MessageId == created.Message.Id);
        Assert.Equal(recipient, job.RecipientUserId);
        Assert.Equal(room.UserId, job.SenderUserId);
        Assert.NotEqual(job.SenderUserId, job.RecipientUserId);
        Assert.True(await db.ChatMessages.AnyAsync(row => row.Id == job.MessageId));
    }

    [Fact]
    public async Task Throw_after_flushed_message_rolls_back_outbox_and_body_together()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        await SeedRecipientAsync(room, room.UserId + 1);

        // 실행
        await Assert.ThrowsAsync<InvalidOperationException>(() => Transaction().ExecuteAsync<int>(async (session, token) =>
        {
            var member = await session.GetMemberAsync(room.UserId, room.RoomId, true, token);
            await session.InsertTextAsync(member, "synthetic rollback", [], ChatMySqlFixture.Epoch.AddMinutes(1), token);
            throw new InvalidOperationException("synthetic rollback");
        }, default));

        // 검증
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.False(await db.ChatPushOutbox.AnyAsync(row => row.RoomId == room.RoomId));
        Assert.Equal(2, await db.ChatMessages.CountAsync(row => row.ChatRoomId == room.RoomId));
    }

    [Fact]
    public async Task Only_human_creation_path_enqueues_and_unique_committed_message_cannot_reenqueue_same_recipient()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        var system = ChatMySqlFixture.Message(room.RoomId, null, ChatMySqlFixture.Epoch.AddMinutes(1));
        system.SenderType = "SYSTEM";
        system.MessageType = "SYSTEM";
        var ai = ChatMySqlFixture.Message(room.RoomId, null, ChatMySqlFixture.Epoch.AddMinutes(1));
        ai.SenderType = "AI";
        db.ChatMessages.AddRange(system, ai);
        await db.SaveChangesAsync();
        var job = Job(room);
        db.ChatPushOutbox.Add(job);
        await db.SaveChangesAsync();

        // 실행
        db.ChatPushOutbox.Add(Job(room));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        // 검증: system/AI 직접 저장에는 push trigger가 없고 human outbox는 message+recipient 유일하다.
        await using var observer = await fixture.Contexts.CreateDbContextAsync();
        Assert.False(await observer.ChatPushOutbox.AnyAsync(row => row.MessageId == system.Id || row.MessageId == ai.Id));
        Assert.Single(await observer.ChatPushOutbox.Where(row => row.MessageId == room.FirstId).ToListAsync());
    }

    [Fact]
    public async Task Database_policy_rechecks_read_membership_join_deleted_and_open_ban()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync("OPEN");
        await SeedOpenAsync(room);
        var job = Job(room);
        var store = new EfChatPushStore(fixture.Contexts, new());
        Assert.True(await store.EligibleAsync(job, default));

        // 실행 / 검증: 각 상태를 독립적으로 바꿔 SQL 정책을 통과할 수 없는지 확인한다.
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        var member = await db.ChatRoomMembers.SingleAsync(row => row.Id == room.MemberId);
        member.LastReadMessageId = room.FirstId;
        await db.SaveChangesAsync();
        Assert.False(await store.EligibleAsync(job, default));
        member.LastReadMessageId = null;
        member.Active = false;
        await db.SaveChangesAsync();
        Assert.False(await store.EligibleAsync(job, default));
        member.Active = true;
        member.JoinedAt = ChatMySqlFixture.Epoch.AddMinutes(10);
        await db.SaveChangesAsync();
        Assert.False(await store.EligibleAsync(job, default));
        member.JoinedAt = ChatMySqlFixture.Epoch;
        var message = await db.ChatMessages.SingleAsync(row => row.Id == room.FirstId);
        message.DeletedAt = ChatMySqlFixture.Epoch;
        await db.SaveChangesAsync();
        Assert.False(await store.EligibleAsync(job, default));
        message.DeletedAt = null;
        db.OpenChatBans.Add(new OpenChatBanEntity
        {
            ChatRoomId = room.RoomId, TargetUserId = room.UserId, TargetChatRoomMemberId = room.MemberId,
            TargetMemberCode = "synthetic_member", NicknameSnapshot = "synthetic", LastJoinedAtSnapshot = member.JoinedAt,
            TargetRoleSnapshot = "MEMBER", BannedByMemberId = room.MemberId, BannedByRole = "OWNER",
            BannedAt = ChatMySqlFixture.Epoch, Reason = "synthetic", CreatedAt = ChatMySqlFixture.Epoch, UpdatedAt = ChatMySqlFixture.Epoch
        });
        await db.SaveChangesAsync();
        Assert.False(await store.EligibleAsync(job, default));
    }

    [Fact]
    public async Task Multiworker_claim_fences_expired_outbox_result_and_atomic_installation_expansion()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        long id = await SeedJobAsync(room);
        var store = new EfChatPushStore(fixture.Contexts, new());

        // 실행: 독립 connection 두 개의 경쟁에서 한 worker만 lease를 획득한다.
        var claims = await Task.WhenAll(store.ClaimOutboxAsync(id, default), store.ClaimOutboxAsync(id, default));
        var old = Assert.Single(claims.OfType<ChatPushOutboxEntity>());
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_push_outbox SET lease_expires_at_utc = TIMESTAMPADD(SECOND,-1,UTC_TIMESTAMP(6)) WHERE id = {id}");
        var current = Assert.IsType<ChatPushOutboxEntity>(await store.ClaimOutboxAsync(id, default));

        // 검증: 늦은 expand/finish는 현재 소유권을 덮지 않고 현재 expand만 두 행을 함께 commit한다.
        Assert.False(await store.ExpandAsync(old, [Target()], default));
        Assert.False(await store.FinishOutboxAsync(old, "FAILED", 0, default));
        Assert.True(await store.ExpandAsync(current, [Target()], default));
        Assert.False(await store.ExpandAsync(current, [Target()], default));
        Assert.Single(await db.ChatPushDeliveries.Where(row => row.OutboxId == id).ToListAsync());
        Assert.Equal("EXPANDED", (await db.ChatPushOutbox.AsNoTracking().SingleAsync(row => row.Id == id)).Status);
    }

    [Fact]
    public async Task Delivery_claim_fences_late_result_and_resume_refresh_preserves_attempt_versions()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        long id = await SeedJobAsync(room);
        var store = new EfChatPushStore(fixture.Contexts, new());
        var outbox = (await store.ClaimOutboxAsync(id, default))!;
        await store.ExpandAsync(outbox, [Target()], default);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        long deliveryId = (await db.ChatPushDeliveries.SingleAsync(row => row.OutboxId == id)).Id;
        var claims = await Task.WhenAll(store.ClaimDeliveryAsync(deliveryId, default), store.ClaimDeliveryAsync(deliveryId, default));
        var old = Assert.Single(claims.OfType<ChatPushDeliveryEntity>());

        // 실행
        Assert.True(await store.RefreshTargetAsync(old, Target() with { Generation = 2 }, default));
        Assert.False(await store.RefreshTargetAsync(old, Target() with { Generation = 3, TokenVersion = 2 }, default));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_push_delivery SET lease_expires_at_utc = TIMESTAMPADD(SECOND,-1,UTC_TIMESTAMP(6)) WHERE id = {deliveryId}");
        var current = Assert.IsType<ChatPushDeliveryEntity>(await store.ClaimDeliveryAsync(deliveryId, default));

        // 검증
        Assert.False(await store.FinishDeliveryAsync(old, "RECEIPT", 900, "stale-ticket", true, default));
        Assert.False(await store.RefreshTargetAsync(old, Target() with { Generation = 3 }, default));
        Assert.True(await store.FinishDeliveryAsync(current, "RECEIPT", 900, "current-ticket", true, default));
        var stored = await db.ChatPushDeliveries.AsNoTracking().SingleAsync(row => row.Id == deliveryId);
        Assert.Equal(2, stored.Generation);
        Assert.Equal("current-ticket", stored.TicketId);
        Assert.Equal(0, stored.Attempts);
        Assert.True(stored.ReceiptExpiresAtUtc > DateTime.UtcNow.AddHours(23));
        Assert.DoesNotContain(deliveryId, await store.CandidatesAsync(true, default));
    }

    [Fact]
    public async Task Retry_due_time_and_24_hour_expiry_bound_claims_using_database_clock()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        long id = await SeedJobAsync(room);
        var store = new EfChatPushStore(fixture.Contexts, new());
        var first = (await store.ClaimOutboxAsync(id, default))!;

        // 실행 / 검증: DB due 시각 이전에는 재claim하지 않는다.
        Assert.True(await store.FinishOutboxAsync(first, "PENDING", 30, default));
        Assert.Null(await store.ClaimOutboxAsync(id, default));
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_push_outbox SET due_at_utc = TIMESTAMPADD(SECOND,-1,UTC_TIMESTAMP(6)) WHERE id = {id}");
        var second = (await store.ClaimOutboxAsync(id, default))!;
        Assert.Equal(2, second.Attempts);
        Assert.True(await store.FinishOutboxAsync(second, "PENDING", 0, default));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_push_outbox SET created_at_utc = TIMESTAMPADD(HOUR,-25,UTC_TIMESTAMP(6)) WHERE id = {id}");
        Assert.DoesNotContain(id, await store.CandidatesAsync(false, default));
        Assert.Null(await store.ClaimOutboxAsync(id, default));
        Assert.Equal("EXPIRED", (await db.ChatPushOutbox.AsNoTracking().SingleAsync(row => row.Id == id)).Status);
    }
    [Fact]
    public async Task Late_ticket_acceptance_keeps_fixed_receipt_window_after_old_message_send_TTL()
    {
        // 준비: 하루 가까이 대기한 메시지도 SEND TTL 안에서 첫 ticket을 받을 수 있다.
        var room = await fixture.SeedReadRoomAsync();
        long id = await SeedJobAsync(room);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_push_outbox SET created_at_utc = TIMESTAMPADD(MINUTE,-1430,UTC_TIMESTAMP(6)) WHERE id = {id}");
        var store = new EfChatPushStore(fixture.Contexts, new());
        var outbox = (await store.ClaimOutboxAsync(id, default))!;
        await store.ExpandAsync(outbox, [Target()], default);
        long deliveryId = (await db.ChatPushDeliveries.SingleAsync(row => row.OutboxId == id)).Id;
        var send = (await store.ClaimDeliveryAsync(deliveryId, default))!;

        // 실행: ticket을 영속화하고 메시지 원래 TTL은 지난 상태로 만들지만 receipt 만료 시각은 유지한다.
        Assert.True(await store.FinishDeliveryAsync(send, "RECEIPT", 900, "late-ticket", true, default));
        var accepted = await db.ChatPushDeliveries.AsNoTracking().SingleAsync(row => row.Id == deliveryId);
        Assert.True(accepted.ReceiptExpiresAtUtc > DateTime.UtcNow.AddHours(23));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_push_delivery SET created_at_utc = TIMESTAMPADD(HOUR,-25,UTC_TIMESTAMP(6)), due_at_utc = TIMESTAMPADD(SECOND,-1,UTC_TIMESTAMP(6)) WHERE id = {deliveryId}");
        Assert.Contains(deliveryId, await store.CandidatesAsync(true, default));
        var receipt = Assert.IsType<ChatPushDeliveryEntity>(await store.ClaimDeliveryAsync(deliveryId, default));
        Assert.True(await store.FinishDeliveryAsync(receipt, "RECEIPT", 900, receipt.TicketId, false, default));

        // 검증: receipt 재시도는 24시간 deadline을 연장하지 않으며 고정 deadline이 지나면 종료한다.
        var retried = await db.ChatPushDeliveries.AsNoTracking().SingleAsync(row => row.Id == deliveryId);
        Assert.Equal(accepted.ReceiptExpiresAtUtc, retried.ReceiptExpiresAtUtc);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_push_delivery SET receipt_expires_at_utc = TIMESTAMPADD(SECOND,-1,UTC_TIMESTAMP(6)), due_at_utc = TIMESTAMPADD(SECOND,-1,UTC_TIMESTAMP(6)) WHERE id = {deliveryId}");
        Assert.DoesNotContain(deliveryId, await store.CandidatesAsync(true, default));
        Assert.Null(await store.ClaimDeliveryAsync(deliveryId, default));
        Assert.Equal("EXPIRED", (await db.ChatPushDeliveries.AsNoTracking().SingleAsync(row => row.Id == deliveryId)).Status);
    }

    [Fact]
    public async Task Send_delivery_without_ticket_expires_at_original_message_deadline()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        long id = await SeedJobAsync(room);
        var store = new EfChatPushStore(fixture.Contexts, new());
        var outbox = (await store.ClaimOutboxAsync(id, default))!;
        await store.ExpandAsync(outbox, [Target()], default);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        long deliveryId = (await db.ChatPushDeliveries.SingleAsync(row => row.OutboxId == id)).Id;

        // 실행
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_push_delivery SET created_at_utc = TIMESTAMPADD(HOUR,-25,UTC_TIMESTAMP(6)) WHERE id = {deliveryId}");

        // 검증
        Assert.DoesNotContain(deliveryId, await store.CandidatesAsync(true, default));
        Assert.Null(await store.ClaimDeliveryAsync(deliveryId, default));
        var expired = await db.ChatPushDeliveries.AsNoTracking().SingleAsync(row => row.Id == deliveryId);
        Assert.Equal("EXPIRED", expired.Status);
        Assert.Null(expired.ReceiptExpiresAtUtc);
    }
    [Fact]
    public async Task Applied_push_schema_uses_utf8mb4_tables_and_binary_ascii_identifiers()
    {
        // 준비: fixture가 검증한 자기 소유 합성 catalog의 실제 metadata만 읽는다.
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        await db.Database.OpenConnectionAsync();
        var tables = new List<PushTableMetadata>();
        var columns = new List<PushColumnMetadata>();
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = """
                SELECT t.TABLE_NAME, c.CHARACTER_SET_NAME, t.TABLE_COLLATION
                FROM information_schema.TABLES t
                JOIN information_schema.COLLATION_CHARACTER_SET_APPLICABILITY c ON c.COLLATION_NAME = t.TABLE_COLLATION
                WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME IN ('chat_push_outbox','chat_push_delivery')
                ORDER BY t.TABLE_NAME
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) tables.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = """
                SELECT TABLE_NAME, COLUMN_NAME, CHARACTER_SET_NAME, COLLATION_NAME
                FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
                  AND TABLE_NAME IN ('chat_push_outbox','chat_push_delivery')
                  AND COLUMN_NAME IN ('event_id','installation_id','account_binding_id','ticket_id','lease_token')
                ORDER BY TABLE_NAME, COLUMN_NAME
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        // 실행 근거는 assertion 전에 남겨 실제 provider DDL 불일치도 숨기지 않는다.
        var manifest = Environment.GetEnvironmentVariable("CHAT_TEST_MANIFEST")!;
        string evidence = Path.Combine(Path.GetDirectoryName(manifest)!, "actual-push-schema-metadata.json");
        await File.WriteAllTextAsync(evidence, JsonSerializer.Serialize(new { tables, columns }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        // 검증: lease_token은 두 테이블에 있으므로 이름 다섯 종류가 실제 column 여섯 개다.
        Assert.Equal(2, tables.Count);
        Assert.All(tables, row => { Assert.Equal("utf8mb4", row.CharacterSet); Assert.Equal("utf8mb4_0900_ai_ci", row.Collation); });
        Assert.Equal(6, columns.Count);
        Assert.All(columns, row => { Assert.Equal("ascii", row.CharacterSet); Assert.Equal("ascii_bin", row.Collation); });
    }

    [Fact]
    public async Task Binary_lease_comparison_rejects_case_changed_owner_token()
    {
        // 준비
        var room = await fixture.SeedReadRoomAsync();
        long id = await SeedJobAsync(room);
        var store = new EfChatPushStore(fixture.Contexts, new());
        var claim = (await store.ClaimOutboxAsync(id, default))!;
        const string exact = "synthetic-case-sensitive-lease";
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_push_outbox SET lease_token = {exact} WHERE id = {id}");
        claim.LeaseToken = exact.ToUpperInvariant();

        // 실행 / 검증: SQL 비교에서도 대소문자가 다른 소유자가 finish할 수 없어야 한다.
        Assert.False(await store.FinishOutboxAsync(claim, "FAILED", 0, default));
        claim.LeaseToken = exact;
        Assert.True(await store.FinishOutboxAsync(claim, "SUPPRESSED", 0, default));
    }

    private sealed record PushTableMetadata(string Table, string CharacterSet, string Collation);
    private sealed record PushColumnMetadata(string Table, string Column, string CharacterSet, string Collation);
    private EfChatMessageTransaction Transaction() => new(fixture.Contexts, new Profiles(), new Delivery(), NullLogger<EfChatMessageTransaction>.Instance);
    private async Task SeedRecipientAsync(SeededReadRoom room, long userId)
    {
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        db.ChatRoomMembers.Add(new() { ChatRoomId = room.RoomId, UserId = userId, Role = "MEMBER", JoinedAt = ChatMySqlFixture.Epoch,
            CreatedAt = ChatMySqlFixture.Epoch, UpdatedAt = ChatMySqlFixture.Epoch });
        await db.SaveChangesAsync();
    }
    private async Task SeedOpenAsync(SeededReadRoom room)
    {
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        db.OpenChatRooms.Add(new() { ChatRoomId = room.RoomId, Visibility = "PUBLIC", MaxMemberCount = 10, Status = "ACTIVE",
            CreatedAt = ChatMySqlFixture.Epoch, UpdatedAt = ChatMySqlFixture.Epoch });
        await db.SaveChangesAsync();
    }
    private static ChatPushOutboxEntity Job(SeededReadRoom room) => new()
    { MessageId = room.FirstId, RoomId = room.RoomId, SenderUserId = room.UserId + 1, RecipientUserId = room.UserId,
        EventId = Guid.NewGuid().ToString("D"), CreatedAtUtc = DateTime.UtcNow, DueAtUtc = DateTime.UtcNow.AddMinutes(-1) };
    private async Task<long> SeedJobAsync(SeededReadRoom room)
    { await using var db = await fixture.Contexts.CreateDbContextAsync(); var job = Job(room); db.ChatPushOutbox.Add(job); await db.SaveChangesAsync(); return job.Id; }
    private static ChatCorePushTarget Target() => new(401, "c2a1b179-bbe5-4be9-a8c9-6206767d4d60", "synthetic_binding", 1, 1, "ExpoPushToken[synthetic-token]", "android");
    private sealed class Profiles : IChatMessageProfileReader
    {
        public Task<string> GetAuditIdentityAsync(long id, CancellationToken token) => Task.FromResult("synthetic@example.invalid");
        public Task<ChatUserMessageProfile> GetUserAsync(long id, CancellationToken token) => throw new InvalidOperationException("Unused synthetic port.");
        public Task<string?> ResolveObjectUrlAsync(string key, CancellationToken token) => throw new InvalidOperationException("Unused synthetic port.");
    }
    private sealed class Delivery : IChatMessageEventDelivery
    {
        public Task ValidateAvailabilityAsync(IReadOnlyList<ChatMessageIntent> intents, CancellationToken token) => Task.CompletedTask;
        public Task DeliverAsync(ChatMessageIntent intent, CancellationToken token) => Task.CompletedTask;
    }
}
