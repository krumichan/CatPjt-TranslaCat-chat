using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.Presence;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.OpenRooms;

public sealed partial class EfOpenRoomStore(
    IDbContextFactory<ChatDbContext> contexts,
    ILogger<EfOpenRoomStore> logger,
    IChatRoomAccountReader? accounts = null,
    IOpenProfileStorage? storage = null,
    IOpenProfileEventDelivery? events = null,
    ChatPresenceCoordinator? presence = null) : IOpenRoomStore
{
    public async Task<OpenRoomDetail> CreateAsync(long userId, OpenRoomValidatedCreate request,
        DateTime now, CancellationToken cancellationToken)
    {
        var account = accounts ?? throw new OpenRoomDependencyUnavailableException();
        await account.EnsureUserExistsAsync(userId, cancellationToken);
        var auditor = await AuditAsync(userId, cancellationToken);

        // 방/OPEN 설정/OWNER membership/profile을 동일 transaction에서 생성한다.
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var room = new ChatRoomEntity
        {
            RoomType = "OPEN",
            SourceType = "MANUAL",
            Name = request.Name,
            Description = request.Description,
            OwnerId = userId,
            Active = true,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = auditor,
            UpdatedBy = auditor
        };
        context.ChatRooms.Add(room);
        await context.SaveChangesAsync(cancellationToken);
        context.OpenChatRooms.Add(new OpenChatRoomEntity
        {
            ChatRoomId = room.Id,
            Visibility = request.Visibility,
            MaxMemberCount = request.MaxMemberCount,
            Status = "ACTIVE",
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = auditor,
            UpdatedBy = auditor
        });
        var defaults = await context.UserChatLanguageSettings.SingleOrDefaultAsync(setting => setting.UserId == userId, cancellationToken);
        var member = new ChatRoomMemberEntity
        {
            ChatRoomId = room.Id,
            UserId = userId,
            Role = "OWNER",
            JoinedAt = now,
            OriginalLanguageCode = defaults?.OriginalLanguageCode ?? "ko",
            TranslationLanguageCode = defaults?.TranslationLanguageCode ?? "ja",
            ShowOriginal = defaults?.ShowOriginal ?? true,
            ShowTranslation = defaults?.ShowTranslation ?? true,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = auditor,
            UpdatedBy = auditor
        };
        context.ChatRoomMembers.Add(member);
        await context.SaveChangesAsync(cancellationToken);

        string code = await GenerateCodeAsync(context, cancellationToken);
        context.OpenChatMemberProfiles.Add(new OpenChatMemberProfileEntity
        {
            ChatRoomMemberId = member.Id,
            MemberCode = code,
            Nickname = request.Nickname,
            ProfileImageObjectKey = OpenRoomPolicy.NormalizeObjectKey(request.ProfileImageObjectKey, member.Id),
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = auditor,
            UpdatedBy = auditor
        });
        await context.SaveChangesAsync(cancellationToken);

        // 새 엔티티의 메모리 시각은 원본처럼 유지한다. 후속 GET의 DATETIME(6) 값과 구분한다.
        var response = await DetailAsync(context, userId, room.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    public async Task<OpenRoomDetail> GetDetailAsync(long userId, long roomId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        return await DetailAsync(context, userId, roomId, cancellationToken);
    }

    // membership 명령도 같은 transaction의 상태로 응답을 만들고 projection 실패 시 rollback한다.
    public Task<OpenRoomDetail> GetDetailWithinAsync(ChatDbContext context, long userId, long roomId, CancellationToken cancellationToken)
    {
        return DetailAsync(context, userId, roomId, cancellationToken);
    }

    public async Task<OpenRoomList> ListAsync(long userId, string? keyword, long? cursorId, int size, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        var query = from open in context.OpenChatRooms
                    join room in context.ChatRooms on open.ChatRoomId equals room.Id
                    where open.Visibility == "PUBLIC" && open.Status == "ACTIVE" && room.Active && room.DeletedAt == null
                    select new
                    {
                        Open = open,
                        Room = room
                    };
        if (cursorId is not null)
        {
            query = query.Where(pair => pair.Room.Id < cursorId.Value);
        }
        if (keyword is not null)
        {
            // Contains는 parameterized LIKE로 번역되며 %/_를 검색어 문자로 보존한다.
            var lowered = keyword.ToLowerInvariant();
            query = query.Where(pair => (pair.Room.Name != null && pair.Room.Name.ToLower().Contains(lowered))
                || (pair.Room.Description != null && pair.Room.Description.ToLower().Contains(lowered)));
        }
        var fetched = await query.OrderByDescending(pair => pair.Room.Id).Take(size + 1).ToListAsync(cancellationToken);
        var page = fetched.Take(size).ToArray();
        if (page.Length == 0)
        {
            return new(Array.Empty<OpenRoomListItem>(), null, false);
        }

        // 원본처럼 page의 ID 집합으로 집계한다. 방마다 상세 조회를 반복하지 않는다.
        var ids = page.Select(pair => pair.Room.Id).ToArray();
        var counts = await context.ChatRoomMembers.Where(member => ids.Contains(member.ChatRoomId)
            && member.Active && member.DeletedAt == null).GroupBy(member => member.ChatRoomId)
            .Select(group => new { Id = group.Key, Count = group.LongCount() }).ToDictionaryAsync(row => row.Id, row => row.Count, cancellationToken);
        var joined = (await context.ChatRoomMembers.Where(member => ids.Contains(member.ChatRoomId) && member.UserId == userId
            && member.Active && member.DeletedAt == null).Select(member => member.ChatRoomId).ToListAsync(cancellationToken)).ToHashSet();
        var banned = (await context.OpenChatBans.Where(ban => ids.Contains(ban.ChatRoomId) && ban.TargetUserId == userId
            && ban.ReleasedAt == null).Select(ban => ban.ChatRoomId).ToListAsync(cancellationToken)).ToHashSet();
        var owners = (await Profiles(context).Where(row => ids.Contains(row.Member.ChatRoomId) && row.Member.Role == "OWNER"
            && row.Member.Active && row.Member.DeletedAt == null).ToListAsync(cancellationToken))
            .GroupBy(row => row.Member.ChatRoomId).ToDictionary(group => group.Key, group => group.First());
        var activities = await context.ChatMessages.Where(message => ids.Contains(message.ChatRoomId)
            && message.Status == "SENT" && message.DeletedAt == null).GroupBy(message => message.ChatRoomId)
            .Select(group => new { Id = group.Key, At = group.Max(message => message.CreatedAt) })
            .ToDictionaryAsync(row => row.Id, row => row.At, cancellationToken);
        var aiCounts = await context.ChatRoomAiMembers.Where(member => ids.Contains(member.ChatRoomId)
            && member.Active && member.DeletedAt == null).GroupBy(member => member.ChatRoomId)
            .Select(group => new { Id = group.Key, Count = group.Count() }).ToDictionaryAsync(row => row.Id, row => row.Count, cancellationToken);
        var disclosures = await context.ChatRoomAiSettings.Where(setting => ids.Contains(setting.ChatRoomId))
            .ToDictionaryAsync(setting => setting.ChatRoomId, setting => setting.DisclosureType, cancellationToken);
        var items = new List<OpenRoomListItem>();
        foreach (var pair in page)
        {
            // 원본 목록은 차단된 사용자에게도 owner 공개 프로필을 보여 준다. 상세의 숨김과 구분한다.
            long roomId = pair.Room.Id;
            long count = counts.GetValueOrDefault(roomId);
            int aiCount = aiCounts.GetValueOrDefault(roomId);
            var blocked = OpenRoomPolicy.JoinBlockedReason(banned.Contains(roomId), pair.Open.Status == "CLOSED",
                joined.Contains(roomId), count, pair.Open.MaxMemberCount);
            var owner = owners.TryGetValue(roomId, out var row) ? await MapProfileAsync(row, null, cancellationToken) : null;
            items.Add(new(roomId, pair.Room.RoomType, pair.Room.SourceType, pair.Room.Name, pair.Room.Description,
                pair.Open.Visibility, pair.Open.Status, count, pair.Open.MaxMemberCount, joined.Contains(roomId),
                blocked == "NONE", blocked, activities.GetValueOrDefault(roomId, pair.Room.UpdatedAt), owner,
                new(aiCount > 0, aiCount, aiCount == 0 ? null : disclosures.GetValueOrDefault(roomId, "PUBLIC"))));
        }
        bool hasNext = fetched.Count > size;
        return new(items.AsReadOnly(), hasNext ? page[^1].Room.Id : null, hasNext);
    }

    private async Task<OpenRoomDetail> DetailAsync(ChatDbContext context, long userId, long roomId, CancellationToken token)
    {
        var pair = await (from open in context.OpenChatRooms
                          join room in context.ChatRooms on open.ChatRoomId equals room.Id
                          where room.Id == roomId && room.Active && room.DeletedAt == null
                          select new
                          {
                              Open = open,
                              Room = room
                          }).SingleOrDefaultAsync(token)
            ?? throw OpenRoomPolicy.Error("OPEN 채팅방을 찾을 수 없습니다.", "ROOM_NOT_FOUND");
        long count = await context.ChatRoomMembers.LongCountAsync(member => member.ChatRoomId == roomId
            && member.Active && member.DeletedAt == null, token);
        var me = await Profiles(context).SingleOrDefaultAsync(row => row.Member.ChatRoomId == roomId
            && row.Member.UserId == userId && row.Member.DeletedAt == null, token);
        bool joined = await context.ChatRoomMembers.AnyAsync(member => member.ChatRoomId == roomId
            && member.UserId == userId && member.Active && member.DeletedAt == null, token);
        bool banned = await context.OpenChatBans.AnyAsync(ban => ban.ChatRoomId == roomId
            && ban.TargetUserId == userId && ban.ReleasedAt == null, token);
        var activity = await context.ChatMessages.Where(message => message.ChatRoomId == roomId
            && message.Status == "SENT" && message.DeletedAt == null)
            .MaxAsync(message => (DateTime?)message.CreatedAt, token);
        var aiCount = await context.ChatRoomAiMembers.CountAsync(member => member.ChatRoomId == roomId
            && member.Active && member.DeletedAt == null, token);
        var disclosure = aiCount == 0 ? null : await DisclosureAsync(context, roomId, token);
        var blocked = OpenRoomPolicy.JoinBlockedReason(banned, pair.Open.Status == "CLOSED", joined, count, pair.Open.MaxMemberCount);

        return new(pair.Room.Id, pair.Room.RoomType, pair.Room.SourceType, pair.Room.Name, pair.Room.Description,
            pair.Open.Visibility, pair.Open.Status, count, pair.Open.MaxMemberCount, joined, blocked == "NONE", blocked,
            !banned && me?.Member.Active == true ? me.Member.Role : null,
            banned ? null : await OwnerAsync(context, roomId, token),
            banned || me is null ? null : await MapProfileAsync(me, null, token),
            activity ?? pair.Room.UpdatedAt, pair.Room.CreatedAt, pair.Room.UpdatedAt,
            new(aiCount > 0, aiCount, disclosure));
    }

    private async Task<OpenProfile?> OwnerAsync(ChatDbContext context, long roomId, CancellationToken token)
    {
        var row = await Profiles(context).FirstOrDefaultAsync(row => row.Member.ChatRoomId == roomId
            && row.Member.Role == "OWNER" && row.Member.Active && row.Member.DeletedAt == null, token);
        return row is null ? null : await MapProfileAsync(row, null, token);
    }

    private static async Task<string> GenerateCodeAsync(ChatDbContext context, CancellationToken token)
    {
        for (int attempt = 0; attempt < OpenRoomPolicy.MemberCodeAttempts; attempt++)
        {
            var code = OpenRoomPolicy.CreateMemberCodeCandidate(RandomNumberGenerator.GetInt32);
            if (!await context.OpenChatMemberProfiles.AnyAsync(profile => profile.MemberCode == code, token))
            {
                return code;
            }
        }
        throw OpenRoomPolicy.Error("OPEN 채팅 멤버 코드를 생성할 수 없습니다.", "MEMBER_CODE_GENERATION_FAILED");
    }

    private async Task<string> AuditAsync(long userId, CancellationToken token)
    {
        var identity = await (accounts ?? throw new OpenRoomDependencyUnavailableException()).GetAuditIdentityAsync(userId, token);
        return identity is { Length: <= 50 } ? identity : throw new OpenRoomDependencyUnavailableException();
    }

    private static IQueryable<ProfileRow> Profiles(ChatDbContext context)
    {
        return from profile in context.OpenChatMemberProfiles
               join member in context.ChatRoomMembers on profile.ChatRoomMemberId equals member.Id
               select new ProfileRow { Profile = profile, Member = member };
    }

    private async Task<OpenProfile> MapProfileAsync(ProfileRow row, bool? online, CancellationToken token)
    {
        return new(row.Member.Id, row.Profile.MemberCode, row.Profile.Nickname,
            await UrlAsync(row.Profile.ProfileImageObjectKey, token), row.Member.Role, row.Member.Active, online, row.Member.JoinedAt);
    }

    private Task<string?> UrlAsync(string? key, CancellationToken token)
    {
        return ChatMessageText.IsBlank(key)
        ? Task.FromResult<string?>(null)
        : (storage ?? throw new OpenRoomDependencyUnavailableException()).ResolveUrlAsync(key!, token);
    }

    private static async Task<string> DisclosureAsync(ChatDbContext context, long roomId, CancellationToken token)
    {
        return await context.ChatRoomAiSettings.Where(setting => setting.ChatRoomId == roomId)
            .Select(setting => setting.DisclosureType).SingleOrDefaultAsync(token) ?? "PUBLIC";
    }

    private sealed class ProfileRow
    {
        public OpenChatMemberProfileEntity Profile { get; init; } = null!;
        public ChatRoomMemberEntity Member { get; init; } = null!;
    }
}
