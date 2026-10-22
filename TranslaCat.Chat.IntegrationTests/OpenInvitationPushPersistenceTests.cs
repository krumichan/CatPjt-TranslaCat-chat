using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Infrastructure.Push;

namespace TranslaCat.Chat.IntegrationTests;

[Collection(ChatMySqlCollection.Name)]
public sealed class OpenInvitationPushPersistenceTests(ChatMySqlFixture fixture)
{
    [Fact]
    public async Task Multiworker_claim_fences_expired_outbox_result_and_atomic_installation_expansion()
    {
        // 준비
        var room = await SeedInvitationAsync();
        long id = room.Job.Id;
        var store = new EfOpenInvitationPushStore(fixture.Contexts, new());

        // 실행: 독립 connection 두 개의 경쟁에서 한 worker만 lease를 획득한다.
        var claims = await Task.WhenAll(store.ClaimOutboxAsync(id, default), store.ClaimOutboxAsync(id, default));
        var old = Assert.Single(claims.OfType<OpenInvitationPushOutboxEntity>());
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE open_invitation_push_outbox SET lease_expires_at_utc = TIMESTAMPADD(SECOND,-1,UTC_TIMESTAMP(6)) WHERE id = {id}");
        var current = Assert.IsType<OpenInvitationPushOutboxEntity>(await store.ClaimOutboxAsync(id, default));

        // 검증: 늦은 expand/finish는 현재 소유권을 덮지 않고 현재 expand만 두 행을 함께 commit한다.
        Assert.False(await store.ExpandAsync(old, [Target()], default));
        Assert.False(await store.FinishOutboxAsync(old, "FAILED", 0, default));
        Assert.True(await store.ExpandAsync(current, [Target()], default));
        Assert.False(await store.ExpandAsync(current, [Target()], default));
        Assert.Single(await db.OpenInvitationPushDeliveries.Where(row => row.OutboxId == id).ToListAsync());
        Assert.Equal("EXPANDED", (await db.OpenInvitationPushOutbox.AsNoTracking().SingleAsync(row => row.Id == id)).Status);
    }

    [Fact]
    public async Task Delivery_claim_fences_late_result_and_resume_refresh_preserves_attempt_versions()
    {
        // 준비
        var room = await SeedInvitationAsync();
        long id = room.Job.Id;
        var store = new EfOpenInvitationPushStore(fixture.Contexts, new());
        var outbox = (await store.ClaimOutboxAsync(id, default))!;
        await store.ExpandAsync(outbox, [Target()], default);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        long deliveryId = (await db.OpenInvitationPushDeliveries.SingleAsync(row => row.OutboxId == id)).Id;
        var claims = await Task.WhenAll(store.ClaimDeliveryAsync(deliveryId, default), store.ClaimDeliveryAsync(deliveryId, default));
        var old = Assert.Single(claims.OfType<OpenInvitationPushDeliveryEntity>());

        // 실행
        Assert.True(await store.RefreshTargetAsync(old, Target() with { Generation = 2 }, default));
        Assert.False(await store.RefreshTargetAsync(old, Target() with { Generation = 3, TokenVersion = 2 }, default));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE open_invitation_push_delivery SET lease_expires_at_utc = TIMESTAMPADD(SECOND,-1,UTC_TIMESTAMP(6)) WHERE id = {deliveryId}");
        var current = Assert.IsType<OpenInvitationPushDeliveryEntity>(await store.ClaimDeliveryAsync(deliveryId, default));

        // 검증
        Assert.False(await store.FinishDeliveryAsync(old, "RECEIPT", 900, "stale-ticket", true, default));
        Assert.False(await store.RefreshTargetAsync(old, Target() with { Generation = 3 }, default));
        Assert.True(await store.FinishDeliveryAsync(current, "RECEIPT", 900, "current-ticket", true, default));
        var stored = await db.OpenInvitationPushDeliveries.AsNoTracking().SingleAsync(row => row.Id == deliveryId);
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
        var room = await SeedInvitationAsync();
        long id = room.Job.Id;
        var store = new EfOpenInvitationPushStore(fixture.Contexts, new());
        var first = (await store.ClaimOutboxAsync(id, default))!;

        // 실행 / 검증: DB due 시각 이전에는 재claim하지 않는다.
        Assert.True(await store.FinishOutboxAsync(first, "PENDING", 30, default));
        Assert.Null(await store.ClaimOutboxAsync(id, default));
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE open_invitation_push_outbox SET due_at_utc = TIMESTAMPADD(SECOND,-1,UTC_TIMESTAMP(6)) WHERE id = {id}");
        var second = (await store.ClaimOutboxAsync(id, default))!;
        Assert.Equal(2, second.Attempts);
        Assert.True(await store.FinishOutboxAsync(second, "PENDING", 0, default));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE open_invitation_push_outbox SET created_at_utc = TIMESTAMPADD(HOUR,-25,UTC_TIMESTAMP(6)) WHERE id = {id}");
        Assert.DoesNotContain(id, await store.CandidatesAsync(false, default));
        Assert.Null(await store.ClaimOutboxAsync(id, default));
        Assert.Equal("EXPIRED", (await db.OpenInvitationPushOutbox.AsNoTracking().SingleAsync(row => row.Id == id)).Status);
    }
    [Fact]
    public async Task Late_ticket_acceptance_keeps_fixed_receipt_window_after_old_message_send_TTL()
    {
        // 준비: 하루 가까이 대기한 메시지도 SEND TTL 안에서 첫 ticket을 받을 수 있다.
        var room = await SeedInvitationAsync();
        long id = room.Job.Id;
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE open_invitation_push_outbox SET created_at_utc = TIMESTAMPADD(MINUTE,-1430,UTC_TIMESTAMP(6)) WHERE id = {id}");
        var store = new EfOpenInvitationPushStore(fixture.Contexts, new());
        var outbox = (await store.ClaimOutboxAsync(id, default))!;
        await store.ExpandAsync(outbox, [Target()], default);
        long deliveryId = (await db.OpenInvitationPushDeliveries.SingleAsync(row => row.OutboxId == id)).Id;
        var send = (await store.ClaimDeliveryAsync(deliveryId, default))!;

        // 실행: ticket을 영속화하고 메시지 원래 TTL은 지난 상태로 만들지만 receipt 만료 시각은 유지한다.
        Assert.True(await store.FinishDeliveryAsync(send, "RECEIPT", 900, "late-ticket", true, default));
        var accepted = await db.OpenInvitationPushDeliveries.AsNoTracking().SingleAsync(row => row.Id == deliveryId);
        Assert.True(accepted.ReceiptExpiresAtUtc > DateTime.UtcNow.AddHours(23));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE open_invitation_push_delivery SET created_at_utc = TIMESTAMPADD(HOUR,-25,UTC_TIMESTAMP(6)), due_at_utc = TIMESTAMPADD(SECOND,-1,UTC_TIMESTAMP(6)) WHERE id = {deliveryId}");
        Assert.Contains(deliveryId, await store.CandidatesAsync(true, default));
        var receipt = Assert.IsType<OpenInvitationPushDeliveryEntity>(await store.ClaimDeliveryAsync(deliveryId, default));
        Assert.True(await store.FinishDeliveryAsync(receipt, "RECEIPT", 900, receipt.TicketId, false, default));

        // 검증: receipt 재시도는 24시간 deadline을 연장하지 않으며 고정 deadline이 지나면 종료한다.
        var retried = await db.OpenInvitationPushDeliveries.AsNoTracking().SingleAsync(row => row.Id == deliveryId);
        Assert.Equal(accepted.ReceiptExpiresAtUtc, retried.ReceiptExpiresAtUtc);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE open_invitation_push_delivery SET receipt_expires_at_utc = TIMESTAMPADD(SECOND,-1,UTC_TIMESTAMP(6)), due_at_utc = TIMESTAMPADD(SECOND,-1,UTC_TIMESTAMP(6)) WHERE id = {deliveryId}");
        Assert.DoesNotContain(deliveryId, await store.CandidatesAsync(true, default));
        Assert.Null(await store.ClaimDeliveryAsync(deliveryId, default));
        Assert.Equal("EXPIRED", (await db.OpenInvitationPushDeliveries.AsNoTracking().SingleAsync(row => row.Id == deliveryId)).Status);
    }

    [Fact]
    public async Task Send_delivery_without_ticket_expires_at_original_message_deadline()
    {
        // 준비
        var room = await SeedInvitationAsync();
        long id = room.Job.Id;
        var store = new EfOpenInvitationPushStore(fixture.Contexts, new());
        var outbox = (await store.ClaimOutboxAsync(id, default))!;
        await store.ExpandAsync(outbox, [Target()], default);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        long deliveryId = (await db.OpenInvitationPushDeliveries.SingleAsync(row => row.OutboxId == id)).Id;

        // 실행
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE open_invitation_push_delivery SET created_at_utc = TIMESTAMPADD(HOUR,-25,UTC_TIMESTAMP(6)) WHERE id = {deliveryId}");

        // 검증
        Assert.DoesNotContain(deliveryId, await store.CandidatesAsync(true, default));
        Assert.Null(await store.ClaimDeliveryAsync(deliveryId, default));
        var expired = await db.OpenInvitationPushDeliveries.AsNoTracking().SingleAsync(row => row.Id == deliveryId);
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
                WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME IN ('open_invitation_push_outbox','open_invitation_push_delivery')
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
                  AND TABLE_NAME IN ('open_invitation_push_outbox','open_invitation_push_delivery')
                  AND COLUMN_NAME IN ('event_id','installation_id','account_binding_id','ticket_id','lease_token')
                ORDER BY TABLE_NAME, COLUMN_NAME
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        // 실행 근거는 assertion 전에 남겨 실제 provider DDL 불일치도 숨기지 않는다.
        var manifest = Environment.GetEnvironmentVariable("CHAT_TEST_MANIFEST")!;
        string evidence = Path.Combine(Path.GetDirectoryName(manifest)!, "actual-open-invitation-push-schema-metadata.json");
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
        var room = await SeedInvitationAsync();
        long id = room.Job.Id;
        var store = new EfOpenInvitationPushStore(fixture.Contexts, new());
        var claim = (await store.ClaimOutboxAsync(id, default))!;
        const string exact = "synthetic-case-sensitive-lease";
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE open_invitation_push_outbox SET lease_token = {exact} WHERE id = {id}");
        claim.LeaseToken = exact.ToUpperInvariant();

        // 실행 / 검증: SQL 비교에서도 대소문자가 다른 소유자가 finish할 수 없어야 한다.
        Assert.False(await store.FinishOutboxAsync(claim, "FAILED", 0, default));
        claim.LeaseToken = exact;
        Assert.True(await store.FinishOutboxAsync(claim, "SUPPRESSED", 0, default));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("read-at")]
    [InlineData("deleted-notification")]
    [InlineData("changed-recipient")]
    [InlineData("changed-actor")]
    [InlineData("wrong-kind")]
    [InlineData("wrong-schema")]
    [InlineData("wrong-room-type")]
    [InlineData("duplicate-json-key")]
    [InlineData("invalid-json")]
    [InlineData("sender-left")]
    [InlineData("sender-rejoined")]
    [InlineData("recipient-joined")]
    [InlineData("recipient-banned")]
    [InlineData("sender-banned")]
    [InlineData("closed")]
    [InlineData("inactive-room")]
    [InlineData("deleted-room")]
    public async Task Current_source_identity_read_open_membership_and_ban_policy_suppresses_stale_invitation(string scenario)
    {
        // 준비: 실제 MySQL source notification과 영속 outbox를 만든다.
        var seeded = await SeedInvitationAsync();
        var job = seeded.Job;
        var store = new EfOpenInvitationPushStore(fixture.Contexts, new());
        Assert.True(await store.EligibleAsync(job, default));
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        var notification = await db.ChatNotifications.SingleAsync(row => row.Id == job.NotificationId);
        var sender = await db.ChatRoomMembers.SingleAsync(row => row.Id == seeded.Room.MemberId);
        var room = await db.ChatRooms.SingleAsync(row => row.Id == job.RoomId);
        var open = await db.OpenChatRooms.SingleAsync(row => row.ChatRoomId == job.RoomId);

        // 실행: 전송 직전 저장 상태가 바뀌면 과거 허용 판정을 재사용하지 않는다.
        switch (scenario)
        {
            case "read": notification.IsRead = true; break;
            case "read-at": notification.ReadAt = DateTime.UtcNow; break;
            case "deleted-notification": notification.DeletedAt = DateTime.UtcNow; break;
            case "changed-recipient": notification.RecipientUserId += 10; break;
            case "changed-actor": notification.ActorUserId += 10; break;
            case "wrong-kind": notification.PayloadJson = "{\"schemaVersion\":1,\"invitationKind\":\"DIRECT\",\"roomType\":\"OPEN\"}"; break;
            case "wrong-schema": notification.PayloadJson = "{\"schemaVersion\":2,\"invitationKind\":\"OPEN_LINK\",\"roomType\":\"OPEN\"}"; break;
            case "wrong-room-type": notification.PayloadJson = "{\"schemaVersion\":1,\"invitationKind\":\"OPEN_LINK\",\"roomType\":\"GROUP\"}"; break;
            case "duplicate-json-key": notification.PayloadJson = "{\"schemaVersion\":1,\"invitationKind\":\"OTHER\",\"invitationKind\":\"OPEN_LINK\",\"roomType\":\"OPEN\"}"; break;
            case "invalid-json": notification.PayloadJson = "{"; break;
            case "sender-left": sender.Active = false; break;
            case "sender-rejoined": sender.JoinedAt = notification.CreatedAt.AddMinutes(1); break;
            case "recipient-joined":
                db.ChatRoomMembers.Add(Member(job.RoomId, job.RecipientUserId));
                break;
            case "recipient-banned":
            case "sender-banned":
                long targetUser = scenario == "sender-banned" ? job.SenderUserId : job.RecipientUserId;
                var target = sender;
                if (targetUser == job.RecipientUserId)
                {
                    target = Member(job.RoomId, targetUser);
                    target.Active = false;
                    db.ChatRoomMembers.Add(target);
                    await db.SaveChangesAsync();
                }
                db.OpenChatBans.Add(new()
                {
                    ChatRoomId = job.RoomId, TargetUserId = targetUser, TargetChatRoomMemberId = target.Id,
                    TargetMemberCode = "OC-SYNTH", NicknameSnapshot = "synthetic", LastJoinedAtSnapshot = target.JoinedAt,
                    TargetRoleSnapshot = "MEMBER", BannedByMemberId = sender.Id, BannedByRole = "OWNER",
                    BannedAt = DateTime.UtcNow, Reason = "synthetic", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                });
                break;
            case "closed": open.Status = "CLOSED"; break;
            case "inactive-room": room.Active = false; break;
            case "deleted-room": room.DeletedAt = DateTime.UtcNow; break;
            default: throw new InvalidOperationException("Unknown synthetic scenario.");
        }
        await db.SaveChangesAsync();

        // 검증
        Assert.False(await store.EligibleAsync(job, default));
    }

    [Fact]
    public async Task Source_notification_has_exactly_one_outbox_and_expansion_rejects_duplicate_or_other_recipient_installations()
    {
        // 준비
        var seed = await SeedInvitationAsync();
        var store = new EfOpenInvitationPushStore(fixture.Contexts, new());
        var claim = (await store.ClaimOutboxAsync(seed.Job.Id, default))!;

        // 실행 / 검증: registry 검증을 건너뛰어 직접 호출해도 다른 수신자나 중복 설치를 저장하지 않는다.
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExpandAsync(claim, [Target() with { UserId = 23 }], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ExpandAsync(claim, [Target(), Target()], default));
        await using (var rejected = await fixture.Contexts.CreateDbContextAsync())
        {
            Assert.Empty(await rejected.OpenInvitationPushDeliveries.Where(row => row.OutboxId == seed.Job.Id).ToListAsync());
            Assert.Equal("PENDING", (await rejected.OpenInvitationPushOutbox.SingleAsync(row => row.Id == seed.Job.Id)).Status);
        }
        await using (var duplicate = await fixture.Contexts.CreateDbContextAsync())
        {
            duplicate.OpenInvitationPushOutbox.Add(new()
            {
                NotificationId = seed.Job.NotificationId, RoomId = seed.Job.RoomId, SenderUserId = seed.Job.SenderUserId,
                RecipientUserId = seed.Job.RecipientUserId, EventId = Guid.NewGuid().ToString("D"),
                CreatedAtUtc = DateTime.UtcNow, DueAtUtc = DateTime.UtcNow
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
        }

        // 실행 / 검증: 유효 확장은 두 설치의 별도 delivery와 outbox 상태를 함께 commit한다.
        Assert.True(await store.ExpandAsync(claim, [Target(), Target() with { InstallationId = "e205aa9f-7a40-4d7f-9249-7af77645b334" }], default));
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(2, await db.OpenInvitationPushDeliveries.CountAsync(row => row.OutboxId == seed.Job.Id));
        Assert.Equal("EXPANDED", (await db.OpenInvitationPushOutbox.SingleAsync(row => row.Id == seed.Job.Id)).Status);
    }

    private sealed record InvitationSeed(SeededReadRoom Room, OpenInvitationPushOutboxEntity Job);

    private async Task<InvitationSeed> SeedInvitationAsync()
    {
        // 준비: 기존 fixture의 실제 합성 catalog에 OPEN source를 추가한다. 외부 계정/기기는 사용하지 않는다.
        var room = await fixture.SeedReadRoomAsync("OPEN", 11);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        db.OpenChatRooms.Add(new()
        {
            ChatRoomId = room.RoomId, Visibility = "PUBLIC", MaxMemberCount = 10, Status = "ACTIVE",
            CreatedAt = ChatMySqlFixture.Epoch, UpdatedAt = ChatMySqlFixture.Epoch
        });
        var notification = new ChatNotificationEntity
        {
            RecipientUserId = 22, ActorUserId = room.UserId, ChatRoomId = room.RoomId,
            NotificationType = "CHAT_INVITATION", PayloadJson = "{\"schemaVersion\":1,\"invitationKind\":\"OPEN_LINK\",\"roomType\":\"OPEN\"}",
            SourceEventKey = "open-link:synthetic:" + Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow
        };
        db.ChatNotifications.Add(notification);
        await db.SaveChangesAsync();
        var job = new OpenInvitationPushOutboxEntity
        {
            NotificationId = notification.Id, RoomId = room.RoomId, SenderUserId = room.UserId, RecipientUserId = 22,
            EventId = Guid.NewGuid().ToString("D"), CreatedAtUtc = DateTime.UtcNow, DueAtUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        db.OpenInvitationPushOutbox.Add(job);
        await db.SaveChangesAsync();
        return new(room, job);
    }

    private static ChatRoomMemberEntity Member(long roomId, long userId) => new()
    {
        ChatRoomId = roomId, UserId = userId, Role = "MEMBER", JoinedAt = ChatMySqlFixture.Epoch,
        CreatedAt = ChatMySqlFixture.Epoch, UpdatedAt = ChatMySqlFixture.Epoch
    };

    private static ChatCorePushTarget Target() => new(22, "c2a1b179-bbe5-4be9-a8c9-6206767d4d60", "synthetic_binding", 1, 1, "ExpoPushToken[synthetic-token]", "android");
    private sealed record PushTableMetadata(string Table, string CharacterSet, string Collation);
    private sealed record PushColumnMetadata(string Table, string Column, string CharacterSet, string Collation);
}
