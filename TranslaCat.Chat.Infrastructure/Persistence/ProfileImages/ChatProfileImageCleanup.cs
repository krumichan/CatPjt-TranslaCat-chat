using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using TranslaCat.Chat.Application.ProfileImages;

namespace TranslaCat.Chat.Infrastructure.Persistence.ProfileImages;

internal static class ChatProfileImageCleanup
{
    public static async Task RollbackAsync(IDbContextTransaction transaction, bool commitAttempted,
        IChatProfileImageObjectStore? objects, string? newKey, ILogger logger)
    {
        // COMMIT 응답 유실은 결과 불명이다. 참조된 object를 지울 위험 때문에 자동 삭제하지 않는다.
        if (commitAttempted)
        {
            logger.LogWarning("Chat image commit result is unknown; object cleanup requires reconciliation.");
            return;
        }
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Chat image rollback was not confirmed ({FailureType}); object is retained.", exception.GetType().Name);
            return;
        }
        if (objects is not null)
        {
            await DeleteAsync(objects, newKey, logger);
        }
    }

    public static async Task DeleteAsync(IChatProfileImageObjectStore objects, string? key, ILogger logger)
    {
        if (ChatProfileImagePolicy.IsJavaBlank(key))
        {
            return;
        }
        try
        {
            // 요청 취소 뒤에도 이미 확정된 저장/rollback의 보상 작업을 시도한다.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await objects.DeleteAsync(key!, deadline.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Chat image object cleanup failed ({FailureType}).", exception.GetType().Name);
        }
    }
}
