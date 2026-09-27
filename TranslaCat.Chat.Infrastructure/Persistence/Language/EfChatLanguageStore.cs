using Microsoft.EntityFrameworkCore;
using TranslaCat.Chat.Application.Language;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Infrastructure.Persistence.Entities;

namespace TranslaCat.Chat.Infrastructure.Persistence.Language;

public sealed class EfChatLanguageStore(
    IDbContextFactory<ChatDbContext> contexts,
    IChatMessageProfileReader? profiles,
    Func<DateTime> clock) : IChatLanguageStore
{
    public async Task<T> ExecuteAsync<T>(bool write, Func<IChatLanguageSession, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        await using var context = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = write ? await context.Database.BeginTransactionAsync(cancellationToken) : null;
        var result = await work(new Session(context, profiles, clock), cancellationToken);
        if (write)
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction!.CommitAsync(cancellationToken);
        }
        return result;
    }

    private sealed class Session(ChatDbContext context, IChatMessageProfileReader? profiles, Func<DateTime> clock) : IChatLanguageSession
    {
        public async Task<ChatLanguageValues?> GetDefaultAsync(long userId, CancellationToken cancellationToken)
        {
            return await context.UserChatLanguageSettings.Where(value => value.UserId == userId)
                        .Select(value => new ChatLanguageValues(value.OriginalLanguageCode, value.TranslationLanguageCode,
                            value.ShowOriginal, value.ShowTranslation)).SingleOrDefaultAsync(cancellationToken);
        }

        public async Task<ChatRoomLanguageState> GetRoomAsync(long userId, long roomId, CancellationToken cancellationToken)
        {
            return await context.ChatRoomMembers.Where(value => value.UserId == userId && value.ChatRoomId == roomId
                            && value.Active && value.DeletedAt == null)
                        .Select(value => new ChatRoomLanguageState(value.Id, value.OriginalLanguageCode, value.TranslationLanguageCode,
                            value.ShowOriginal, value.ShowTranslation)).SingleOrDefaultAsync(cancellationToken)
                        ?? throw new ChatLanguageException("채팅방 접근 권한이 없습니다.", "CHAT_ROOM_ACCESS_DENIED");
        }

        public async Task SaveDefaultAsync(long userId, ChatLanguageValues value, CancellationToken cancellationToken)
        {
            // UserService.getById와 감사 identity의 외부 경계를 인증되지 않은 고정 값으로 대체하지 않는다.
            var audit = await AuditAsync(userId, cancellationToken);
            var entity = await context.UserChatLanguageSettings.SingleOrDefaultAsync(row => row.UserId == userId, cancellationToken);
            var now = clock();
            if (entity is null)
            {
                entity = new UserChatLanguageSettingEntity { UserId = userId, CreatedAt = now, CreatedBy = audit };
                context.UserChatLanguageSettings.Add(entity);
            }
            entity.OriginalLanguageCode = value.OriginalLanguageCode;
            entity.TranslationLanguageCode = value.TranslationLanguageCode;
            entity.ShowOriginal = value.ShowOriginal;
            entity.ShowTranslation = value.ShowTranslation;
            entity.UpdatedAt = now;
            entity.UpdatedBy = audit;
        }

        public async Task SaveRoomAsync(long userId, long memberId, ChatLanguageValues? value, CancellationToken cancellationToken)
        {
            var audit = await AuditAsync(userId, cancellationToken);
            var entity = await context.ChatRoomMembers.SingleAsync(row => row.Id == memberId && row.UserId == userId
                && row.Active && row.DeletedAt == null, cancellationToken);

            // 언어 필드만 바꾸고 cursor/role/멤버십은 건드리지 않는다. reset은 언어 null과 표시 true를 복원한다.
            entity.OriginalLanguageCode = value?.OriginalLanguageCode;
            entity.TranslationLanguageCode = value?.TranslationLanguageCode;
            entity.ShowOriginal = value?.ShowOriginal ?? true;
            entity.ShowTranslation = value?.ShowTranslation ?? true;
            entity.UpdatedAt = clock();
            entity.UpdatedBy = audit;
        }

        private async Task<string> AuditAsync(long userId, CancellationToken cancellationToken)
        {
            if (profiles is null)
            {
                throw new ChatLanguageDependencyUnavailableException();
            }
            return await profiles.GetAuditIdentityAsync(userId, cancellationToken);
        }
    }
}
