using System.Threading.Channels;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Translation;

namespace TranslaCat.Chat.Api.Translation;

public sealed class ChatTranslationQueue(ChatTranslationOptions options, Func<bool>? resourcesConfigured = null) : IChatTranslationDispatcher
{
    private readonly Channel<ChatTranslationRequestedIntent> channel = Channel.CreateBounded<ChatTranslationRequestedIntent>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

    // 등록 여부만 확인한다. DB/Redis 연결성이나 실제 AI 상태를 probe한 결과는 아니다.
    public bool IsConfigured => options.IsConfigured && (resourcesConfigured?.Invoke() ?? true);

    public async Task DispatchAsync(ChatTranslationRequestedIntent intent, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new ChatMessageDependencyUnavailableException("chat translation dispatcher");
        }

        // 큐는 빠른 통지 수단이다. 프로세스 종료/취소로 유실된 통지는 DB PENDING sweep이 복구한다.
        var snapshot = intent with
        {
            TranslationIds = Array.AsReadOnly(intent.TranslationIds.Distinct().ToArray())
        };
        await channel.Writer.WriteAsync(snapshot, cancellationToken);
    }

    public IAsyncEnumerable<ChatTranslationRequestedIntent> ReadAllAsync(CancellationToken cancellationToken)
    {
        return channel.Reader.ReadAllAsync(cancellationToken);
    }
}
