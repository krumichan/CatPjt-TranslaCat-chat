using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Application.OpenMembership;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;
using TranslaCat.Chat.Infrastructure.Persistence.OpenRooms;

namespace TranslaCat.Chat.Infrastructure.Persistence.OpenMembership;

public sealed partial class EfOpenMembershipStore(
    IDbContextFactory<ChatDbContext> contexts,
    EfOpenRoomStore rooms,
    IOpenMembershipDelivery delivery,
    ILogger<EfOpenMembershipStore> logger,
    IChatRoomAccountReader? accounts = null,
    IOpenProfileStorage? storage = null) : IOpenMembershipStore
{
    public Task<OpenRoomDetail> JoinAsync(long userId, long roomId, OpenOwnerProfile? profile, DateTime now, CancellationToken token)
    {
        return ExecuteAsync(roomId, async (db, open, intents) =>
        {
            // 정원과 복귀 판단은 같은 OPEN 설정 row 잠금으로 직렬화한다.
            EnsureOpen(open);
            if (await db.OpenChatBans.AnyAsync(ban => ban.ChatRoomId == roomId && ban.TargetUserId == userId && ban.ReleasedAt == null, token))
            {
                throw Error("해당 OPEN 채팅방에서 차단되어 참여할 수 없습니다.", "BANNED");
            }
            var member = await db.ChatRoomMembers.SingleOrDefaultAsync(row => row.ChatRoomId == roomId && row.UserId == userId, token);
            if (member?.Active == true)
            {
                // 삭제된 active row는 정상 membership으로 취급하지 않는다. 원본의 비정상 상태 허용을 복제하지 않는다.
                if (member.DeletedAt is not null)
                {
                    throw Error("OPEN 채팅방 멤버가 아니거나 접근 권한이 없습니다.", "MEMBER_ACCESS_DENIED");
                }

                return await rooms.GetDetailWithinAsync(db, userId, roomId, token);
            }
            if (await db.ChatRoomMembers.LongCountAsync(row => row.ChatRoomId == roomId && row.Active && row.DeletedAt == null, token) >= open.MaxMemberCount)
            {
                throw Error("OPEN 채팅방 정원이 가득 찼습니다.", "ROOM_FULL");
            }

            var audit = await AuditAsync(userId, token);
            var latest = await db.ChatMessages.Where(row => row.ChatRoomId == roomId && row.Status == "SENT" && row.DeletedAt == null)
                .MaxAsync(row => (long?)row.Id, token);
            var defaults = await db.UserChatLanguageSettings.SingleOrDefaultAsync(row => row.UserId == userId, token);
            OpenChatMemberProfileEntity savedProfile;
            bool restoring = member is not null;
            if (restoring)
            {
                savedProfile = await ProfileAsync(db, member!.Id, token);
            }
            else
            {
                if (profile is null)
                {
                    throw Error("최초 참여 시 OPEN 프로필은 필수입니다.", "JOIN_PROFILE_REQUIRED");
                }

                await Accounts().EnsureUserExistsAsync(userId, token);
                member = new ChatRoomMemberEntity { ChatRoomId = roomId, UserId = userId, CreatedAt = now, CreatedBy = audit };
                db.ChatRoomMembers.Add(member);
                savedProfile = new OpenChatMemberProfileEntity { CreatedAt = now, CreatedBy = audit };
            }

            // 재참여는 새 참여 구간이다. 이전 cursor를 이어 쓰지 않고 현재 마지막 메시지로 초기화한다.
            member!.Role = "MEMBER";
            member.Active = true;
            member.JoinedAt = now;
            member.LeftAt = null;
            member.DeletedAt = null;
            member.OriginalLanguageCode = defaults?.OriginalLanguageCode ?? "ko";
            member.TranslationLanguageCode = defaults?.TranslationLanguageCode ?? "ja";
            member.ShowOriginal = defaults?.ShowOriginal ?? true;
            member.ShowTranslation = defaults?.ShowTranslation ?? true;
            member.LastReadMessageId = latest;
            member.LastReadAt = latest is null ? null : now;
            Touch(member, now, audit);
            await db.SaveChangesAsync(token);

            if (profile is not null)
            {
                var nickname = OpenRoomPolicy.NormalizeNickname(profile.Nickname);
                var key = OpenRoomPolicy.NormalizeObjectKey(profile.ProfileImageObjectKey, member.Id);
                if (restoring && key is not null && key != savedProfile.ProfileImageObjectKey)
                {
                    throw Error("재참여 시 프로필 이미지는 참여 완료 후 이미지 API로 변경해야 합니다.", "PROFILE_IMAGE_OBJECT_KEY_INVALID");
                }
                savedProfile.Nickname = nickname;
                savedProfile.UpdatedAt = now;
                savedProfile.UpdatedBy = audit;
                if (!restoring)
                {
                    savedProfile.ProfileImageObjectKey = key;
                }
            }
            if (!restoring)
            {
                savedProfile.ChatRoomMemberId = member.Id;
                savedProfile.MemberCode = await GenerateCodeAsync(db, token);
                db.OpenChatMemberProfiles.Add(savedProfile);
            }
            await db.SaveChangesAsync(token);

            // projection/storage 실패도 commit 전에 확인한다. 이벤트는 성공한 commit 뒤에만 전달한다.
            if (restoring && profile is not null)
            {
                var view = await MapProfileAsync(member, savedProfile, token);
                intents.Add(new OpenMemberProfileChanged(new(roomId, member.Id, view.MemberCode, view.Nickname, view.ProfileImageUrl, view.Role, now)));
            }
            intents.Add(new OpenMembersChanged(roomId, now));
            return await rooms.GetDetailWithinAsync(db, userId, roomId, token);
        }, token);
    }

    public Task<OpenMembershipResult> LeaveAsync(long userId, long roomId, DateTime now, CancellationToken token)
    {
        return ExecuteAsync(roomId, async (db, open, intents) =>
        {
            var member = await ActiveMemberAsync(db, userId, roomId, token);
            if (member.Role == "OWNER")
            {
                if (await db.ChatRoomMembers.LongCountAsync(row => row.ChatRoomId == roomId && row.Active && row.DeletedAt == null, token) > 1)
                {
                    throw Error("OWNER는 다른 멤버에게 OWNER를 위임한 뒤 퇴실해야 합니다.", "OWNER_TRANSFER_REQUIRED");
                }

                throw Error("유일한 OWNER는 방 나가기 대신 OPEN 채팅방을 종료해야 합니다.", "OWNER_CLOSE_REQUIRED");
            }

            var profile = await ProfileAsync(db, member.Id, token);
            member.Role = "MEMBER";
            member.Active = false;
            member.LeftAt = now;
            Touch(member, now, await AuditAsync(userId, token));
            await db.SaveChangesAsync(token);
            intents.Add(new OpenMembersChanged(roomId, now));
            return new OpenMembershipResult(roomId, false, member.Role, await MapProfileAsync(member, profile, token));
        }, token);
    }

    private async Task<T> ExecuteAsync<T>(long roomId,
        Func<ChatDbContext, OpenChatRoomEntity, List<OpenMembershipIntent>, Task<T>> work, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var locked = roomId <= 0 ? [] : await db.OpenChatRooms
            .FromSqlInterpolated($"SELECT * FROM open_chat_room WHERE chat_room_id = {roomId} FOR UPDATE").ToListAsync(token);
        var open = locked.SingleOrDefault() ?? throw Error("OPEN 채팅방을 찾을 수 없습니다.", "ROOM_NOT_FOUND");
        var intents = new List<OpenMembershipIntent>();
        var response = await work(db, open, intents);
        await delivery.ValidateAvailabilityAsync(intents, token);
        await transaction.CommitAsync(token);

        // 원본 AFTER_COMMIT listener처럼 전달 실패가 이미 commit된 membership을 되돌리지 않는다.
        foreach (var intent in intents)
        {
            try
            {
                await delivery.DeliverAsync(intent, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogWarning("OPEN membership delivery failed ({FailureType}).", exception.GetType().Name);
            }
        }
        return response;
    }

    private static Task<ChatRoomMemberEntity> ActiveMemberAsync(ChatDbContext db, long userId, long roomId, CancellationToken token)
    {
        return RequiredMemberAsync(db.ChatRoomMembers.Where(row => row.ChatRoomId == roomId && row.UserId == userId && row.Active && row.DeletedAt == null), token);
    }

    private static async Task<ChatRoomMemberEntity> RequiredMemberAsync(IQueryable<ChatRoomMemberEntity> query, CancellationToken token)
    {
        return await query.SingleOrDefaultAsync(token) ?? throw Error("OPEN 채팅방 멤버가 아니거나 접근 권한이 없습니다.", "MEMBER_ACCESS_DENIED");
    }

    private static async Task<OpenChatMemberProfileEntity> ProfileAsync(ChatDbContext db, long memberId, CancellationToken token)
    {
        return await db.OpenChatMemberProfiles.SingleOrDefaultAsync(row => row.ChatRoomMemberId == memberId, token)
        ?? throw Error("OPEN 채팅 프로필을 찾을 수 없습니다.", "PROFILE_NOT_FOUND");
    }

    private async Task<OpenProfile> MapProfileAsync(ChatRoomMemberEntity member, OpenChatMemberProfileEntity profile, CancellationToken token)
    {
        return new(member.Id, profile.MemberCode, profile.Nickname, profile.ProfileImageObjectKey is null ? null
            : await (storage ?? throw new OpenRoomDependencyUnavailableException()).ResolveUrlAsync(profile.ProfileImageObjectKey, token),
            member.Role, member.Active, null, member.JoinedAt);
    }

    private static async Task<string> GenerateCodeAsync(ChatDbContext db, CancellationToken token)
    {
        for (int attempt = 0; attempt < OpenRoomPolicy.MemberCodeAttempts; attempt++)
        {
            var code = OpenRoomPolicy.CreateMemberCodeCandidate(RandomNumberGenerator.GetInt32);
            if (!await db.OpenChatMemberProfiles.AnyAsync(row => row.MemberCode == code, token))
            {
                return code;
            }
        }
        throw Error("OPEN 채팅 멤버 코드를 생성할 수 없습니다.", "MEMBER_CODE_GENERATION_FAILED");
    }

    private IChatRoomAccountReader Accounts()
    {
        return accounts ?? throw new OpenRoomDependencyUnavailableException();
    }

    private async Task<string> AuditAsync(long userId, CancellationToken token)
    {
        var identity = await Accounts().GetAuditIdentityAsync(userId, token);
        return identity is { Length: <= 50 } ? identity : throw new OpenRoomDependencyUnavailableException();
    }

    private static void Touch(ChatRoomMemberEntity member, DateTime now, string audit)
    {
        member.UpdatedAt = now;
        member.UpdatedBy = audit;
    }

    private static void EnsureOpen(OpenChatRoomEntity open)
    {
        if (open.Status == "CLOSED")
        {
            throw Error("종료된 OPEN 채팅방에는 참여할 수 없습니다.", "ROOM_CLOSED");
        }
    }

    private static void EnsureOwner(ChatRoomMemberEntity member)
    {
        if (member.Role != "OWNER")
        {
            throw Error("OPEN 채팅방 OWNER만 수행할 수 있습니다.", "OWNER_ONLY");
        }
    }

    private static OpenRoomException Error(string message, string suffix)
    {
        return new(message, "OPEN_CHAT_" + suffix);
    }
}
