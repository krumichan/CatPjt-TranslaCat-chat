using System.Data;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.ProfileImages;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Application.Safety;
using TranslaCat.Chat.Infrastructure.Core;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Infrastructure.Persistence.OpenRooms;
using TranslaCat.Chat.Infrastructure.Safety;

namespace TranslaCat.Chat.IntegrationTests;

public sealed partial class SafetyPersistenceTests
{
    private static readonly ChatProfileImageUpload JournalPng = new("image/png", [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

    [Fact]
    public async Task OPEN_journal_commits_before_PUT_and_worker_and_deletion_wait_for_actual_upload_parent()
    {
        // 준비: 실제 OPEN store/DB gate/journal을 통과하고 외부 object provider만 합성으로 둔다.
        var seed = await SeedAsync(61001, "OPEN");
        var clock = new FixedTime();
        var handler = new JournalDeleteHandler();
        var journal = Journal(handler, clock);
        var ports = new JournalPorts { PausePut = true };
        string oldKey = await SetImageAsync(seed, "old.png");
        ports.BeforePut = async key =>
        {
            await using var check = await fixture.Contexts.CreateDbContextAsync();
            var durable = await check.ChatOpenProfileUploadIntents.Where(row => row.UserId == seed.UserId).ToListAsync();
            Assert.Equal(2, durable.Count);
            Assert.Equal("PUT_STARTED", Assert.Single(durable, row => row.ObjectKey == key).State);
            Assert.Equal("PUT_CONFIRMED", Assert.Single(durable, row => row.ObjectKey == oldKey).State);
        };
        var store = ImageStore(handler, ports, journal);

        // 실행: PUT 도중 worker/탈퇴가 시작돼도 parent gate가 끝나기 전에는 정리할 수 없다.
        var upload = store.UploadImageAsync(seed.UserId, seed.RoomId, JournalPng, Now, default);
        await ports.PutReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var scan = journal.ProcessDueAsync(seed.UserId, default);
        var cleanup = ImageDeletion(handler, clock, journal);
        var operation = ImageOperation();
        var deletion = cleanup.DeleteAsync(seed.UserId, operation, default);
        await Task.Delay(150);
        Assert.False(scan.IsCompleted);
        Assert.False(deletion.IsCompleted);
        Assert.Empty(handler.Deleted);
        ports.PutResume.TrySetResult();
        await upload.WaitAsync(TimeSpan.FromSeconds(15));
        await scan.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("COMPLETED", (await deletion.WaitAsync(TimeSpan.FromSeconds(15))).Status);

        // 검증: 이전/새 개인 객체만 실제 DELETE ACK 뒤 끝나고 늦은 mutation은 차단된다.
        Assert.Contains(oldKey, handler.Deleted);
        Assert.Contains(ports.NewKey!, handler.Deleted);
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        Assert.False(await read.ChatOpenProfileUploadIntents.AnyAsync(row => row.UserId == seed.UserId));
        Assert.False(await read.OpenChatMemberProfiles.AnyAsync(row => read.ChatRoomMembers.Any(member => member.Id == row.ChatRoomMemberId
            && member.UserId == seed.UserId) && row.ProfileImageObjectKey != null));
        var denied = await Assert.ThrowsAsync<ChatSafetyException>(() => store.UploadImageAsync(seed.UserId, seed.RoomId, JournalPng, Now, default));
        Assert.Equal("CHAT_ACCOUNT_DELETED", denied.Code);
    }

    [Fact]
    public async Task OPEN_uncertain_PUT_survives_restart_and_elapsed_time_until_positive_late_ACK()
    {
        // 준비: provider가 bytes를 수락했지만 응답 유실인 실제 업로드 경계를 만든다.
        var seed = await SeedAsync(62001, "OPEN");
        var clock = new FixedTime();
        var handler = new JournalDeleteHandler();
        var ports = new JournalPorts { FailPut = true };
        var journal = Journal(handler, clock);
        var store = ImageStore(handler, ports, journal);

        // 실행: 요청 rollback 후 새 adapter/cleanup 인스턴스도 미확정 intent를 읽어 PENDING을 유지한다.
        await Assert.ThrowsAsync<IOException>(() => store.UploadImageAsync(seed.UserId, seed.RoomId, JournalPng, Now, default));
        journal = Journal(handler, clock);
        var deletion = ImageDeletion(handler, clock, journal);
        var operation = ImageOperation();
        Assert.Equal("PENDING", (await deletion.DeleteAsync(seed.UserId, operation, default)).Status);
        clock.Instant = clock.Instant.AddDays(365);
        await deletion.ProcessObjectsAsync(seed.UserId, default);
        Assert.Equal("PENDING", (await deletion.DeleteAsync(seed.UserId, operation, default)).Status);
        Assert.Empty(handler.Deleted);
        await using (var read = await fixture.Contexts.CreateDbContextAsync())
        {
            var intent = await read.ChatOpenProfileUploadIntents.SingleAsync(row => row.UserId == seed.UserId);
            Assert.Equal("PUT_UNCERTAIN", intent.State);
            Assert.Null(intent.PutConfirmedAtUtc);
            Assert.Null(intent.NextAttemptAtUtc);
        }

        // 검증: 늦은 실제 PUT ACK는 intent만 깨우며 profile을 재생성하지 않는다.
        await journal.ConfirmPutAsync(seed.UserId, ports.NewKey!, default);
        await deletion.ProcessObjectsAsync(seed.UserId, default);
        Assert.Equal("COMPLETED", (await deletion.DeleteAsync(seed.UserId, operation, default)).Status);
        Assert.Equal(ports.NewKey, Assert.Single(handler.Deleted));
        await using var final = await fixture.Contexts.CreateDbContextAsync();
        Assert.False(await final.ChatRoomMembers.AnyAsync(row => row.UserId == seed.UserId && row.Active));
    }

    [Fact]
    public async Task OPEN_confirmed_PUT_with_parent_rollback_is_recovered_by_fresh_journal_without_deleting_current_old_reference()
    {
        // 준비: PUT/ACK 뒤 DB flush 후 projection 오류는 정상적인 rollback이지만 프로세스 재시작 뒤도 키가 남아야 한다.
        var seed = await SeedAsync(63001, "OPEN");
        string oldKey = await SetImageAsync(seed, "old.png");
        var handler = new JournalDeleteHandler();
        var clock = new FixedTime();
        var ports = new JournalPorts { FailProjection = true };
        var store = ImageStore(handler, ports, Journal(handler, clock));

        // 실행
        await Assert.ThrowsAsync<IOException>(() => store.UploadImageAsync(seed.UserId, seed.RoomId, JournalPng, Now, default));
        await Journal(handler, clock).ProcessDueAsync(seed.UserId, default);

        // 검증: DB에 없는 새 ACK 객체만 지우고 참조된 이전 객체는 보존한다.
        Assert.Equal(ports.NewKey, Assert.Single(handler.Deleted));
        await using var read = await fixture.Contexts.CreateDbContextAsync();
        Assert.Equal(oldKey, (await read.OpenChatMemberProfiles.SingleAsync(row => read.ChatRoomMembers.Any(member => member.Id == row.ChatRoomMemberId
            && member.UserId == seed.UserId))).ProfileImageObjectKey);
        var old = await read.ChatOpenProfileUploadIntents.SingleAsync(row => row.UserId == seed.UserId);
        Assert.Equal(oldKey, old.ObjectKey);
        Assert.Null(old.NextAttemptAtUtc);
    }

    [Fact]
    public async Task OPEN_replacement_and_explicit_delete_keep_failed_old_keys_until_actual_DELETE_ACK_retry()
    {
        // 준비
        var seed = await SeedAsync(64001, "OPEN");
        string oldKey = await SetImageAsync(seed, "old.png");
        var clock = new FixedTime();
        var handler = new JournalDeleteHandler { Available = false };
        var journal = Journal(handler, clock);
        var ports = new JournalPorts();
        var store = ImageStore(handler, ports, journal);

        // 실행: 교체 후 old DELETE 실패와 명시적 이미지 제거 후 new DELETE 실패를 모두 영속화한다.
        await store.UploadImageAsync(seed.UserId, seed.RoomId, JournalPng, Now, default);
        await store.DeleteImageAsync(seed.UserId, seed.RoomId, Now.AddMinutes(1), default);
        var deletion = ImageDeletion(handler, clock, journal);
        var operation = ImageOperation();
        Assert.Equal("PENDING", (await deletion.DeleteAsync(seed.UserId, operation, default)).Status);
        await using (var read = await fixture.Contexts.CreateDbContextAsync())
        {
            var pending = await read.ChatOpenProfileUploadIntents.Where(row => row.UserId == seed.UserId).ToListAsync();
            Assert.Equal(2, pending.Count);
            Assert.All(pending, row => Assert.True(row.Attempts > 0));
        }
        Assert.Empty(handler.Deleted);
        handler.Available = true;
        clock.Instant = clock.Instant.AddHours(2);
        await deletion.ProcessObjectsAsync(seed.UserId, default);

        // 검증
        Assert.Equal("COMPLETED", (await deletion.DeleteAsync(seed.UserId, operation, default)).Status);
        Assert.Equal(new[] { oldKey, ports.NewKey! }.Order(), handler.Deleted.Order());
        await using var final = await fixture.Contexts.CreateDbContextAsync();
        Assert.False(await final.ChatOpenProfileUploadIntents.AnyAsync(row => row.UserId == seed.UserId));
    }

    [Fact]
    public async Task OPEN_current_commit_reference_is_positive_evidence_and_shared_other_profile_and_AI_avatar_are_preserved()
    {
        // 준비: parent commit ACK가 유실되어도 실제 참조는 PUT 완료 증거다.
        var seed = await SeedAsync(65001, "OPEN");
        var handler = new JournalDeleteHandler();
        var clock = new FixedTime();
        var journal = Journal(handler, clock);
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        long memberId = await db.ChatRoomMembers.Where(row => row.UserId == seed.UserId).Select(row => row.Id).SingleAsync();
        string key = $"open-chat-profiles/{memberId}/{Guid.NewGuid():D}.png";
        await using (var parent = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted))
        {
            await ChatAccountWriteGate.AcquireAsync(db, [seed.UserId], default);
            await journal.StartAsync(seed.UserId, memberId, key, null, default);
            var profile = await db.OpenChatMemberProfiles.SingleAsync(row => row.ChatRoomMemberId == memberId);
            profile.ProfileImageObjectKey = key;
            await db.SaveChangesAsync();
            await parent.CommitAsync();
        }
        await journal.ProcessDueAsync(seed.UserId, default);
        var intent = await db.ChatOpenProfileUploadIntents.AsNoTracking().SingleAsync(row => row.UserId == seed.UserId);
        Assert.Equal("PUT_CONFIRMED", intent.State);
        Assert.Null(intent.NextAttemptAtUtc);
        Assert.Empty(handler.Deleted);

        // 실행: 타 참여자와 AI의 공유 참조가 남는 경우 개인 연결만 없애고 객체는 보존한다.
        (await db.OpenChatMemberProfiles.SingleAsync(row => row.ChatRoomMemberId == seed.OtherMemberId)).ProfileImageObjectKey = key;
        db.ChatAiAgents.Add(new ChatAiAgentEntity { Nickname = "공유 AI", PersonaPrompt = "synthetic", OriginalLanguageCode = "ko",
            ProfileImageObjectKey = key, CreatedAt = Now, UpdatedAt = Now });
        await db.SaveChangesAsync();
        var deletion = ImageDeletion(handler, clock, journal);
        Assert.Equal("COMPLETED", (await deletion.DeleteAsync(seed.UserId, ImageOperation(), default)).Status);

        // 검증
        Assert.Empty(handler.Deleted);
        Assert.False(await db.ChatOpenProfileUploadIntents.AnyAsync(row => row.UserId == seed.UserId));
        Assert.Equal(key, (await db.OpenChatMemberProfiles.AsNoTracking().SingleAsync(row => row.ChatRoomMemberId == seed.OtherMemberId)).ProfileImageObjectKey);
        Assert.True(await db.ChatAiAgents.AnyAsync(row => row.ProfileImageObjectKey == key));
    }

    [Fact]
    public async Task OPEN_image_missing_journal_fails_before_PUT_and_new_intent_rejects_shared_AI_or_other_member_prefix()
    {
        // 준비 / 실행
        var seed = await SeedAsync(66001, "OPEN");
        var handler = new JournalDeleteHandler();
        var ports = new JournalPorts();
        var store = ImageStore(handler, ports, null);
        await Assert.ThrowsAsync<ChatProfileImageUnavailableException>(() => store.UploadImageAsync(seed.UserId, seed.RoomId, JournalPng, Now, default));
        var journal = Journal(handler, new FixedTime());
        await Assert.ThrowsAsync<OpenRoomException>(() => journal.StartAsync(seed.UserId, seed.OtherMemberId,
            "chat-ai/1/profile/file.png", null, default));
        await Assert.ThrowsAsync<OpenRoomException>(() => journal.StartAsync(seed.UserId, seed.OtherMemberId,
            $"open-chat-profiles/{seed.OtherMemberId + 1}/file.png", null, default));

        // 검증
        Assert.Null(ports.NewKey);
        Assert.Empty(handler.Deleted);
    }

    private EfOpenProfileUploadJournal Journal(JournalDeleteHandler handler, TimeProvider clock)
        => new(fixture.Contexts, JournalCore(handler), clock);
    private static HttpChatCoreClient JournalCore(JournalDeleteHandler handler)
        => new(new HttpClient(handler), new(new Uri("http://127.0.0.1:1"), TimeSpan.FromSeconds(2)), new Tokens());
    private EfChatAccountDeletionStore ImageDeletion(JournalDeleteHandler handler, TimeProvider clock, IOpenProfileUploadJournal journal)
        => new(fixture.Contexts, JournalCore(handler), instant => instant.UtcDateTime, clock, journal);
    private EfOpenRoomStore ImageStore(JournalDeleteHandler handler, JournalPorts ports, IOpenProfileUploadJournal? journal)
        => new(fixture.Contexts, NullLogger<EfOpenRoomStore>.Instance, ports, ports, ports, null,
            new ChatContentPolicy(fixture.Contexts, Core(new CoreHandler(false))), journal);
    private static ChatAccountDeletionRequest ImageOperation() => new(Guid.NewGuid().ToString("D"), new DateTimeOffset(Now, TimeSpan.Zero), "journal@example.invalid");
    private async Task<string> SetImageAsync(Seed seed, string filename)
    {
        await using var db = await fixture.Contexts.CreateDbContextAsync();
        long member = await db.ChatRoomMembers.Where(row => row.UserId == seed.UserId).Select(row => row.Id).SingleAsync();
        string key = $"open-chat-profiles/{member}/{filename}";
        (await db.OpenChatMemberProfiles.SingleAsync(row => row.ChatRoomMemberId == member)).ProfileImageObjectKey = key;
        await db.SaveChangesAsync();
        return key;
    }

    private sealed class JournalDeleteHandler : HttpMessageHandler
    {
        public bool Available { get; set; } = true;
        public List<string> Deleted { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            if (!Available) return new(HttpStatusCode.ServiceUnavailable);
            Deleted.Add(json.RootElement.GetProperty("objectKey").GetString()!);
            return new(HttpStatusCode.NoContent);
        }
    }
    private sealed class JournalPorts : IOpenProfileStorage, IChatProfileImageObjectStore, IChatRoomAccountReader, IOpenProfileEventDelivery
    {
        public bool IsConfigured => true;
        public bool PausePut { get; init; }
        public bool FailPut { get; init; }
        public bool FailProjection { get; init; }
        public string? NewKey { get; private set; }
        public Func<string, Task>? BeforePut { get; set; }
        public TaskCompletionSource PutReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PutResume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task StoreAsync(string key, string type, ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            if (BeforePut is not null) await BeforePut(key);
            NewKey = key;
            PutReached.TrySetResult();
            if (PausePut) await PutResume.Task.WaitAsync(token);
            if (FailPut) throw new IOException("Synthetic unknown PUT response.");
        }
        public Task<string?> ResolveUrlAsync(string key, CancellationToken token)
            => FailProjection ? throw new IOException("Synthetic projection failure after PUT ACK.") : Task.FromResult<string?>("https://synthetic.example.invalid/" + key);
        public Task DeleteAsync(string key, CancellationToken token) => throw new InvalidOperationException("Journal mode must not use untracked compensating DELETE.");
        public Task DeliverAsync(OpenProfileChanged change, CancellationToken token) => Task.CompletedTask;
        public Task EnsureUserExistsAsync(long? userId, CancellationToken token) => Task.CompletedTask;
        public Task<string> GetAuditIdentityAsync(long userId, CancellationToken token) => Task.FromResult("journal@example.invalid");
        public Task<ChatDirectPartner> GetDirectPartnerAsync(long userId, CancellationToken token)
            => Task.FromResult(new ChatDirectPartner(userId, "SYNTHETIC", "합성", null, null, null, null));
    }
}
