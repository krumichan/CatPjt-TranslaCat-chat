using System.Data;
using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Language;
using TranslaCat.Chat.Application.Presence;
using TranslaCat.Chat.Application.Rooms;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Repositories;

public sealed class EfChatRoomStore(
    IDbContextFactory<ChatDbContext> contexts,
    IChatRoomAccountReader? accounts = null,
    ChatPresenceCoordinator? presence = null) : IChatRoomStore
{
    public async Task<long> CreateOrReuseAsync(long userId, ChatRoomCreateRequest request,
        IReadOnlyList<long?> distinctMembers, DateTime now, CancellationToken cancellationToken)
    {
        var directory = accounts ?? throw new ChatRoomDependencyUnavailableException();
        await directory.EnsureUserExistsAsync(userId, cancellationToken);
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        // 원본처럼 MANUAL DIRECT만 재사용하며 정확히 활성 회원 두 명인 가장 이른 방을 선택한다.
        // 원본에 없는 pair unique/분산 lock을 주장하지 않는다. 동시 최초 생성의 중복 위험은 별도 gate다.
        if (request.RoomType == "DIRECT")
        {
            var target = distinctMembers.Single();
            var reusable = await context.ChatRooms.Where(room => room.RoomType == "DIRECT" && room.SourceType == "MANUAL"
                    && room.Active && room.DeletedAt == null
                    && context.ChatRoomMembers.Count(member => member.ChatRoomId == room.Id && member.Active && member.DeletedAt == null) == 2
                    && context.ChatRoomMembers.Any(member => member.ChatRoomId == room.Id && member.UserId == userId && member.Active && member.DeletedAt == null)
                    && context.ChatRoomMembers.Any(member => member.ChatRoomId == room.Id && member.UserId == target && member.Active && member.DeletedAt == null))
                .OrderBy(room => room.Id).Select(room => (long?)room.Id).FirstOrDefaultAsync(cancellationToken);
            if (reusable is long existing)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }
        }

        foreach (var memberId in distinctMembers)
        {
            await directory.EnsureUserExistsAsync(memberId, cancellationToken);
        }
        var auditor = await directory.GetAuditIdentityAsync(userId, cancellationToken);
        if (auditor.Length > 50)
        {
            throw new ChatRoomDependencyUnavailableException();
        }

        // 방과 모든 membership을 한 transaction에서 저장한다. 계정이나 인증 테이블은 생성하지 않는다.
        var roomEntity = new ChatRoomEntity
        {
            RoomType = request.RoomType!,
            SourceType = "MANUAL",
            OwnerId = userId,
            Name = request.RoomType == "DIRECT" ? null : request.Name,
            Description = request.RoomType == "DIRECT" ? null : request.Description,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedBy = auditor,
            UpdatedBy = auditor
        };
        context.ChatRooms.Add(roomEntity);
        await context.SaveChangesAsync(cancellationToken);
        foreach (var memberId in new long?[] { userId }.Concat(distinctMembers))
        {
            if (memberId is null)
            {
                throw new ChatRoomException("사용자를 찾을 수 없습니다.");
            }
            var setting = await context.UserChatLanguageSettings.AsNoTracking()
                .SingleOrDefaultAsync(value => value.UserId == memberId.Value, cancellationToken);
            context.ChatRoomMembers.Add(new ChatRoomMemberEntity
            {
                ChatRoomId = roomEntity.Id,
                UserId = memberId.Value,
                Role = memberId == userId ? "OWNER" : "MEMBER",
                JoinedAt = now,
                OriginalLanguageCode = setting?.OriginalLanguageCode ?? ChatLanguageService.SystemDefault.OriginalLanguageCode,
                TranslationLanguageCode = setting?.TranslationLanguageCode ?? ChatLanguageService.SystemDefault.TranslationLanguageCode,
                ShowOriginal = setting?.ShowOriginal ?? true,
                ShowTranslation = setting?.ShowTranslation ?? true,
                CreatedAt = now,
                UpdatedAt = now,
                CreatedBy = auditor,
                UpdatedBy = auditor
            });
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return roomEntity.Id;
    }

    public async Task<ChatRoomView> GetAsync(long userId, long roomId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var member = await context.ChatRoomMembers.AsNoTracking().SingleOrDefaultAsync(value => value.ChatRoomId == roomId
            && value.UserId == userId && value.Active && value.DeletedAt == null, cancellationToken)
            ?? throw new ChatRoomException("채팅방에 접근할 권한이 없습니다.");
        var room = await context.ChatRooms.AsNoTracking().SingleAsync(value => value.Id == roomId, cancellationToken);
        if (!room.Active || room.DeletedAt is not null)
        {
            throw new ChatRoomException("채팅방을 찾을 수 없습니다.");
        }

        // 같은 DB snapshot의 회원/언어 설정을 조합한다. OPEN 응답에서는 owner user ID를 숨긴다.
        var members = await ActiveMembers(context, roomId).ToListAsync(cancellationToken);
        var defaults = await context.UserChatLanguageSettings.AsNoTracking().SingleOrDefaultAsync(value => value.UserId == userId, cancellationToken);
        var fallback = defaults is null
            ? new ChatLanguageResult(ChatLanguageService.SystemDefault, false, "SYSTEM")
            : new ChatLanguageResult(new(defaults.OriginalLanguageCode, defaults.TranslationLanguageCode, defaults.ShowOriginal, defaults.ShowTranslation), false, "DEFAULT");
        var language = ChatLanguageService.ResolveRoom(new(member.Id, member.OriginalLanguageCode, member.TranslationLanguageCode,
            member.ShowOriginal, member.ShowTranslation), fallback);
        var partner = await PartnerAsync(room, userId, members, cancellationToken);
        return new(room.Id, room.RoomType, room.SourceType, room.Name, room.Description,
            room.RoomType == "OPEN" ? null : room.OwnerId, members.Count, room.Active,
            language.Values.OriginalLanguageCode, language.Values.TranslationLanguageCode, language.RoomLanguageSettingApplied,
            room.CreatedAt, room.UpdatedAt, member.Role, partner);
    }

    public async Task<IReadOnlyList<ChatRoomListItem>> ListAsync(long userId, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var rooms = await context.ChatRooms.AsNoTracking().Where(room => room.Active && room.DeletedAt == null
                && context.ChatRoomMembers.Any(member => member.ChatRoomId == room.Id && member.UserId == userId && member.Active && member.DeletedAt == null)
                && (room.RoomType != "OPEN" || context.OpenChatRooms.Any(open => open.ChatRoomId == room.Id && open.Status == "ACTIVE")))
            .OrderByDescending(room => room.UpdatedAt).ToListAsync(cancellationToken);
        var result = new List<ChatRoomListItem>();
        foreach (var room in rooms)
        {
            var members = await ActiveMembers(context, room.Id).ToListAsync(cancellationToken);
            var mine = members.Single(member => member.UserId == userId);
            var unread = await context.ChatMessages.LongCountAsync(message => message.ChatRoomId == room.Id
                && message.Status == "SENT" && message.DeletedAt == null && message.MessageType != "SYSTEM"
                && message.CreatedAt >= mine.JoinedAt
                && (message.SenderType == "AI" || (message.SenderType == "USER" && message.SenderUserId.HasValue && message.SenderUserId.Value != userId))
                && (mine.LastReadMessageId == null || message.Id > mine.LastReadMessageId), cancellationToken);
            result.Add(new(room.Id, room.RoomType, room.SourceType, room.Name, room.Description,
                room.RoomType == "OPEN" ? null : room.OwnerId, members.Count, unread, room.CreatedAt, room.UpdatedAt,
                await PartnerAsync(room, userId, members, cancellationToken)));
        }
        return result.AsReadOnly();
    }

    private static IQueryable<ChatRoomMemberEntity> ActiveMembers(ChatDbContext context, long roomId)
    {
        return context.ChatRoomMembers.AsNoTracking().Where(member => member.ChatRoomId == roomId && member.Active && member.DeletedAt == null);
    }

    private async Task<ChatDirectPartner?> PartnerAsync(ChatRoomEntity room, long userId, List<ChatRoomMemberEntity> members, CancellationToken cancellationToken)
    {
        if (room.RoomType != "DIRECT" || room.SourceType != "FRIEND")
        {
            return null;
        }
        var partner = members.FirstOrDefault(member => member.UserId != userId);
        if (partner is null)
        {
            return null;
        }

        var profile = await (accounts ?? throw new ChatRoomDependencyUnavailableException()).GetDirectPartnerAsync(partner.UserId, cancellationToken);
        var online = presence is null ? null : await presence.ResolveOnlineAsync(partner.UserId, cancellationToken);
        return profile with
        {
            Online = online
        };
    }
}
