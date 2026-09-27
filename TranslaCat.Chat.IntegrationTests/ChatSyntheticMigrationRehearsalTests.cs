using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Infrastructure.Persistence;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.IntegrationTests.Migration;

namespace TranslaCat.Chat.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ChatSyntheticMigrationCollection
{
    public const string Name = "Owned synthetic migration rehearsal";
}

// 일반 MySQL fixture를 사용하지 않아 기존 test catalog의 migration metadata도 열지 않는다.
[Collection(ChatSyntheticMigrationCollection.Name)]
public sealed class ChatSyntheticMigrationRehearsalTests
{
    private const long RoomId = 9_007_199_254_740_993;
    private const long OwnerMemberId = RoomId + 10;
    private const long PastMemberId = RoomId + 11;
    private const long MessageId = RoomId + 20;
    private const long AiMemberId = RoomId + 30;
    private const long ExternalUserId = long.MaxValue - 100;
    private static readonly DateTime At = new DateTime(2026, 9, 26, 12, 34, 56).AddTicks(1234560);

    [Fact]
    public async Task Owned_synthetic_fourteen_table_copy_preserves_data_rolls_back_failure_and_refuses_rerun()
    {
        // 준비: 기존 test/default catalog에 접근하지 않고 이 실행에서 생성한 source/target만 사용한다.
        var rehearsal = await ChatSyntheticMigrationRehearsal.CreateOwnedAsync();
        await SeedSyntheticSourceAsync(rehearsal);
        await using var source = rehearsal.Source();
        var expected = await ChatSyntheticMigrationRehearsal.DigestsAsync(source);
        Assert.Equal(14, expected.Count);
        Assert.All(expected, table => Assert.True(table.Count > 0));
        Assert.Equal(14, source.Model.GetEntityTypes().Count());

        // 실행 / 검증: 일부 parent table 저장 후 합성 실패를 발생시켜 전체 target transaction rollback을 확인한다.
        var injected = await Assert.ThrowsAsync<InvalidOperationException>(() => rehearsal.CopyAsync(failAfterRoomMembers: true));
        Assert.Contains("Synthetic failure", injected.Message);
        await using (var empty = rehearsal.Target())
        {
            Assert.All(await ChatSyntheticMigrationRehearsal.DigestsAsync(empty), table => Assert.Equal(0, table.Count));
        }

        // 실행: 각 source SELECT와 target INSERT를 독립 context에서 FK 순서대로 수행한다.
        await rehearsal.CopyAsync();
        await using var target = rehearsal.Target();
        var actual = await ChatSyntheticMigrationRehearsal.DigestsAsync(target);

        // 검증: count와 모든 scalar의 정렬 digest로 14개 table을 비교하며 중요한 nullable/정밀도를 직접 확인한다.
        Assert.Equal(expected, actual);
        Assert.Equal(await source.Database.GetAppliedMigrationsAsync(), await target.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await target.Database.GetPendingMigrationsAsync());
        Assert.False(target.Database.HasPendingModelChanges());
        var owner = await target.ChatRoomMembers.SingleAsync(member => member.Id == OwnerMemberId);
        var past = await target.ChatRoomMembers.SingleAsync(member => member.Id == PastMemberId);
        Assert.Equal(ExternalUserId, owner.UserId);
        Assert.Equal(MessageId, owner.LastReadMessageId);
        Assert.Null(owner.LastReadAt);
        Assert.Null(past.LastReadMessageId);
        Assert.Equal(At, past.LastReadAt);
        Assert.Equal(At, past.DeletedAt);
        Assert.False(past.Active);
        Assert.Equal("synthetic/open-chat-profiles/保全😺.png", (await target.OpenChatMemberProfiles.FirstAsync()).ProfileImageObjectKey);
        var activity = await target.ChatRoomAiActivities.SingleAsync();
        Assert.Equal(MessageId, activity.LastHumanMessageId);
        Assert.Equal(AiMemberId, activity.LastRevivalAiMemberId);
        Assert.Equal(long.MaxValue - 5, activity.RevivalCycleVersion);
        Assert.Equal("synthetic-claim", activity.ClaimToken);
        Assert.Equal(At.AddMinutes(5), activity.ClaimExpiresAt);
        Assert.Equal("synthetic-translation-lease", (await target.ChatMessageTranslations.SingleAsync()).ProcessingToken);

        // 공개 user 계정 테이블 또는 다른 catalog를 참조하는 FK가 생성되지 않았는지 실제 metadata로 확인한다.
        Assert.Equal(0, await ScalarAsync(target,
            "SELECT COUNT(*) FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_SCHEMA IS NOT NULL AND REFERENCED_TABLE_SCHEMA <> DATABASE()"));
        Assert.Equal(0, await ScalarAsync(target,
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN ('user','users','account','refresh_token')"));
        Assert.True(await ScalarAsync(target,
            "SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE()") > 0);

        // 재실행은 merge/삭제 없이 거절하고, 복사된 큰 명시적 ID 뒤의 auto increment도 실제 INSERT로 확인한다.
        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() => rehearsal.CopyAsync());
        Assert.Contains("not empty", duplicate.Message);
        await VerifyGeneratedIdentityAndForeignKeyAsync(rehearsal);
        await using var finalTarget = rehearsal.Target();
        Assert.Equal(expected, await ChatSyntheticMigrationRehearsal.DigestsAsync(finalTarget));
        Assert.Equal(expected, await ChatSyntheticMigrationRehearsal.DigestsAsync(source));
        await rehearsal.RecordAsync("VERIFIED_SYNTHETIC_ONLY_PRESERVED", actual);
    }

    [Theory]
    [InlineData("translacat_chat", "translacat_chat_test_11111111111111111111111111111111")]
    [InlineData("translacat", "translacat_chat_test_11111111111111111111111111111111")]
    [InlineData("translacat_chat_test_11111111111111111111111111111111", "translacat_chat_test_11111111111111111111111111111111")]
    [InlineData("translacat_chat_test_22222222222222222222222222222222", "translacat_chat_test_11111111111111111111111111111111")]
    public void Catalog_guard_rejects_default_shared_same_or_previously_registered_targets(string source, string target)
    {
        // 준비
        string[] occupied = ["translacat_chat_test_22222222222222222222222222222222"];

        // 실행 / 검증: DB 접속이나 CREATE 전에 금지된 이름을 거절한다.
        Assert.Throws<InvalidOperationException>(() => ChatSyntheticMigrationRehearsal.ValidateFreshCatalogPair(source, target, occupied));
    }

    private static async Task SeedSyntheticSourceAsync(ChatSyntheticMigrationRehearsal rehearsal)
    {
        await using var context = rehearsal.Source();
        await using var transaction = await context.Database.BeginTransactionAsync();

        // 합성 fixture의 ID는 JS 정수 정밀도 범위를 넘어선다. 실제 BE 데이터나 일반 프로필을 읽지 않는다.
        context.ChatRooms.Add(new ChatRoomEntity
        {
            Id = RoomId,
            RoomType = "OPEN",
            SourceType = "OPEN",
            OwnerId = ExternalUserId,
            Name = "synthetic 이관 😺",
            Description = null,
            CreatedAt = At,
            UpdatedAt = At,
            CreatedBy = "synthetic-owner",
            UpdatedBy = null
        });
        context.ChatAiAgents.Add(new ChatAiAgentEntity
        {
            Id = 101,
            Nickname = "synthetic AI",
            OriginalLanguageCode = "ja",
            PersonaPrompt = "synthetic persona only",
            ProfileImageObjectKey = "synthetic/ai/avatar.png",
            ProfileBackgroundImageObjectKey = null,
            Bio = "합성",
            Active = false,
            DeletedAt = At,
            CreatedAt = At,
            UpdatedAt = At
        });
        context.ChatAiSystemSettings.Add(new ChatAiSystemSettingEntity
        {
            Id = "GLOBAL",
            MaxAiMembersPerRoom = 3,
            ConversationResponseRate = 10,
            ConversationCooldownSeconds = 60,
            ConversationMinHumanMessagesAfterAi = 2,
            ResponseDelayEnabled = true,
            ResponseDelayMinMillis = 10,
            ResponseDelayMaxMillis = 20,
            RevivalFirstDelayHours = 2,
            RevivalSecondDelayHours = 4,
            RevivalThirdDelayHours = 8,
            RevivalAllowedStartTime = TimeSpan.FromHours(9),
            RevivalAllowedEndTime = TimeSpan.FromHours(23),
            ContextMaxMessages = 20,
            ContextMaxCharacters = 8000,
            ReplyMaxCharacters = 300,
            MentionRateLimitCount = 4,
            MentionRateLimitWindowSeconds = 60,
            CreatedAt = At,
            UpdatedAt = At
        });
        context.UserChatLanguageSettings.Add(new UserChatLanguageSettingEntity
        {
            Id = 102,
            UserId = ExternalUserId,
            OriginalLanguageCode = "ko",
            TranslationLanguageCode = "ja",
            ShowOriginal = false,
            ShowTranslation = true,
            CreatedAt = At,
            UpdatedAt = At
        });
        await context.SaveChangesAsync();

        context.ChatRoomMembers.AddRange(
            new ChatRoomMemberEntity
            {
                Id = OwnerMemberId,
                ChatRoomId = RoomId,
                UserId = ExternalUserId,
                Role = "OWNER",
                JoinedAt = At,
                LastReadMessageId = MessageId,
                LastReadAt = null,
                OriginalLanguageCode = null,
                TranslationLanguageCode = "ja",
                CreatedAt = At,
                UpdatedAt = At
            },
            new ChatRoomMemberEntity
            {
                Id = PastMemberId,
                ChatRoomId = RoomId,
                UserId = ExternalUserId - 1,
                Role = "MEMBER",
                JoinedAt = At,
                LastReadMessageId = null,
                LastReadAt = At,
                LeftAt = At,
                DeletedAt = At,
                Active = false,
                ShowOriginal = false,
                ShowTranslation = false,
                CreatedAt = At,
                UpdatedAt = At
            });
        context.OpenChatRooms.Add(new OpenChatRoomEntity
        {
            Id = 103,
            ChatRoomId = RoomId,
            Visibility = "UNLISTED",
            Status = "CLOSED",
            MaxMemberCount = 50,
            ClosedAt = At,
            CreatedAt = At,
            UpdatedAt = At
        });
        context.ChatRoomAiMembers.Add(new ChatRoomAiMemberEntity
        {
            Id = AiMemberId,
            ChatRoomId = RoomId,
            AiAgentId = 101,
            Active = false,
            JoinedAt = At,
            LeftAt = At,
            DeletedAt = At,
            CreatedAt = At,
            UpdatedAt = At
        });
        context.ChatRoomAiSettings.Add(new ChatRoomAiSettingEntity
        {
            Id = 104,
            ChatRoomId = RoomId,
            DisclosureType = "PRIVATE",
            MentionPermission = "OWNER_ADMIN_ONLY",
            ConversationEnabled = true,
            RevivalEnabled = false,
            CreatedAt = At,
            UpdatedAt = At
        });
        await context.SaveChangesAsync();

        context.OpenChatMemberProfiles.Add(new OpenChatMemberProfileEntity
        {
            Id = 105,
            ChatRoomMemberId = PastMemberId,
            MemberCode = "OC-ABCDE",
            Nickname = "합성 과거 프로필",
            ProfileImageObjectKey = "synthetic/open-chat-profiles/保全😺.png",
            CreatedAt = At,
            UpdatedAt = At
        });
        context.ChatMessages.AddRange(
            new ChatMessageEntity
            {
                Id = MessageId,
                ChatRoomId = RoomId,
                SenderUserId = ExternalUserId,
                SenderType = "USER",
                MessageType = "TEXT",
                Content = "합성 메시지 日本語 😺",
                Status = "SENT",
                CreatedAt = At,
                UpdatedAt = At
            },
            new ChatMessageEntity
            {
                Id = MessageId + 1,
                ChatRoomId = RoomId,
                SenderAiMemberId = AiMemberId,
                SenderType = "AI",
                MessageType = "TEXT",
                Content = "합성 삭제 메시지",
                Status = "DELETED",
                AiRequestId = "synthetic-ai-request",
                DeletedAt = At,
                CreatedAt = At,
                UpdatedAt = At
            });
        await context.SaveChangesAsync();

        context.ChatMessageTranslations.Add(new ChatMessageTranslationEntity
        {
            Id = 106,
            ChatMessageId = MessageId,
            LanguageCode = "ja",
            TranslatedContent = null,
            Status = "PENDING",
            FailureReason = null,
            CompletedAt = null,
            ProcessingToken = "synthetic-translation-lease",
            ProcessingExpiresAt = At.AddMinutes(4),
            CreatedAt = At,
            UpdatedAt = At
        });
        context.OpenChatBans.Add(new OpenChatBanEntity
        {
            Id = 107,
            ChatRoomId = RoomId,
            TargetUserId = ExternalUserId - 1,
            TargetChatRoomMemberId = PastMemberId,
            TargetMemberCode = "OC-ABCDE",
            NicknameSnapshot = "snapshot 이름",
            ProfileImageObjectKeySnapshot = "synthetic/snapshot.png",
            LastJoinedAtSnapshot = At,
            TargetRoleSnapshot = "MEMBER",
            BannedByMemberId = OwnerMemberId,
            BannedByRole = "OWNER",
            BannedAt = At,
            Reason = "synthetic reason",
            ReleasedByMemberId = OwnerMemberId,
            ReleasedAt = At.AddMinutes(1),
            CreatedAt = At,
            UpdatedAt = At
        });
        context.ChatRoomAiActivities.Add(new ChatRoomAiActivityEntity
        {
            Id = 108,
            ChatRoomId = RoomId,
            LastHumanMessageId = MessageId,
            LastHumanMessageAt = At,
            RevivalCycleVersion = long.MaxValue - 5,
            RevivalStage = 2,
            LastRevivalAt = At,
            NextRevivalAt = null,
            RevivalStopped = true,
            LastRevivalAiMemberId = AiMemberId,
            ClaimToken = "synthetic-claim",
            ClaimExpiresAt = At.AddMinutes(5),
            CreatedAt = At,
            UpdatedAt = At
        });
        context.ChatNotifications.Add(new ChatNotificationEntity
        {
            Id = 109,
            RecipientUserId = ExternalUserId - 1,
            NotificationType = "OPEN_CHAT_KICKED",
            ChatRoomId = RoomId,
            ActorUserId = ExternalUserId,
            PayloadJson = "{\"roomName\":\"synthetic\",\"reason\":null}",
            SourceEventKey = "synthetic-open-ban:107",
            IsRead = false,
            ReadAt = null,
            DeletedAt = At,
            CreatedAt = At,
            CreatedBy = "synthetic-owner"
        });
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static async Task VerifyGeneratedIdentityAndForeignKeyAsync(ChatSyntheticMigrationRehearsal rehearsal)
    {
        await using (var context = rehearsal.Target())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var message = ChatMySqlFixture.Message(RoomId, ExternalUserId, At);
            context.ChatMessages.Add(message);
            await context.SaveChangesAsync();
            Assert.True(message.Id > MessageId + 1);
            await transaction.RollbackAsync();
        }

        // 실제 FK가 고아 profile을 거절하는지 확인하고 probe 행은 rollback한다.
        await using var invalid = rehearsal.Target();
        await using var invalidTransaction = await invalid.Database.BeginTransactionAsync();
        invalid.OpenChatMemberProfiles.Add(new OpenChatMemberProfileEntity
        {
            ChatRoomMemberId = long.MaxValue,
            MemberCode = "OC-ZYXWV",
            Nickname = "synthetic orphan",
            CreatedAt = At,
            UpdatedAt = At
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => invalid.SaveChangesAsync());
        await invalidTransaction.RollbackAsync();
    }

    private static async Task<long> ScalarAsync(ChatDbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
