using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Infrastructure.Translation;

namespace TranslaCat.Chat.Infrastructure.Ai;

public sealed class HttpChatAiReplyClient(
    HttpClient client, ChatAiOptions options, ChatExternalApiCircuitBreaker circuit) : IChatAiReplyClient
{
    public bool IsConfigured => options.IsConfigured;

    public async Task<ChatAiReplyResponse?> GenerateAsync(ChatAiReplyRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new ChatMessageDependencyUnavailableException("AI chat reply client");
        }

        // 모델 실행20초와 HTTP 여유5초를 합쳐도 전체 재시도가 120초 claim보다 짧다.
        var command = ChatAiExecutionPolicy.Build(request,
            Math.Min(20_000, (int)options.AttemptTimeout.TotalMilliseconds));
        for (int attempt = 1; attempt <= options.MaximumAttempts; attempt++)
        {
            try
            {
                // 번역과 같은 원본 externalApiClient 회로를 공유하고 각 HTTP 시도를 집계한다.
                return await circuit.ExecuteAsync(token => SendOnceAsync(request, command, token), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ChatModelExecutionFailure failure)
            {
                // 입력·인증·refusal은 재시도하지 않고, Retry-After가 예산보다 길면 새 호출을 만들지 않는다.
                if (!failure.Retryable)
                {
                    throw new InvalidOperationException("AI Server Chat Reply Error", failure);
                }

                if (failure.RetryAfterSeconds is int delay && TimeSpan.FromSeconds(delay) > options.RetryWait)
                {
                    throw new ChatExecutionDeferredException(
                        "AI Server Chat Reply Error", TimeSpan.FromSeconds(delay), failure);
                }

                if (attempt == options.MaximumAttempts)
                {
                    throw new InvalidOperationException("AI Server Chat Reply Error", failure);
                }

                await Task.Delay(options.RetryWait, cancellationToken);
            }
            catch (Exception)
            {
                if (attempt == options.MaximumAttempts)
                {
                    throw new InvalidOperationException("AI Server Chat Reply Error");
                }

                await Task.Delay(options.RetryWait, cancellationToken);
            }
        }

        throw new InvalidOperationException("AI Server Chat Reply Error");
    }

    private async Task<ChatAiReplyResponse?> SendOnceAsync(
        ChatAiReplyRequest request, ChatModelExecutionCommand command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(
            Math.Min(options.AttemptTimeout.TotalMilliseconds, 25_000)));
        // 프롬프트·schema·profile은 CHAT 정책이 완성하고 AI에는 범용 실행만 요청한다.
        var result = await ChatModelExecutionTransport.ExecuteAsync(
            client, options.AiBaseUri!, options.ApiKey!, command, timeout.Token);
        return ChatAiExecutionPolicy.Normalize(request, result.Output);
    }
}
