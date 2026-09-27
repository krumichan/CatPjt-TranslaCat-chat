using TranslaCat.Chat.Application.Ai;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.Translation;
using TranslaCat.Chat.Infrastructure.Ai;

namespace TranslaCat.Chat.Infrastructure.Translation;

public sealed class HttpChatTranslationClient : IChatTranslationClient
{
    private readonly HttpClient client;
    private readonly ChatTranslationOptions options;
    private readonly ChatExternalApiCircuitBreaker circuit;

    public HttpChatTranslationClient(HttpClient client, ChatTranslationOptions options,
        ChatExternalApiCircuitBreaker? circuit = null)
    {
        options.Validate();
        this.client = client;
        this.options = options;
        this.circuit = circuit ?? new ChatExternalApiCircuitBreaker(TimeProvider.System);
    }

    public bool IsConfigured => options.IsConfigured;

    public async Task<string> TranslateAsync(string text, string targetLanguageCode, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new ChatMessageDependencyUnavailableException("AI chat translation client");
        }

        // 논리 작업의 traceId와 prompt는 재시도 전체에서 고정한다.
        ChatModelExecutionCommand command = ChatTranslationExecutionPolicy.Build(
            text, targetLanguageCode, (int)options.AttemptTimeout.TotalMilliseconds);
        string? translated = null;
        for (int attempt = 1; attempt <= options.MaximumAttempts; attempt++)
        {
            try
            {
                // 원본 Retry가 CircuitBreaker를 감싼다. 4xx/JSON decode/회로 차단도 기본 재시도 대상이다.
                translated = await circuit.ExecuteAsync(
                    token => SendOnceAsync(command, token), cancellationToken);
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ChatModelExecutionFailure failure)
            {
                // 인증·schema·refusal은 한 번만 판정한다. 긴 Retry-After는 lease를 넘겨 재시도하지 않는다.
                if (!failure.Retryable)
                {
                    throw new InvalidOperationException("AI Server Chat Translation Error", failure);
                }

                if (failure.RetryAfterSeconds is int delay && TimeSpan.FromSeconds(delay) > options.RetryWait)
                {
                    throw new ChatExecutionDeferredException(
                        "AI Server Chat Translation Error", TimeSpan.FromSeconds(delay), failure);
                }

                if (attempt == options.MaximumAttempts)
                {
                    throw new InvalidOperationException("AI Server Chat Translation Error", failure);
                }

                await Task.Delay(options.RetryWait, cancellationToken);
            }
            catch (Exception)
            {
                if (attempt == options.MaximumAttempts)
                {
                    // 주소/인증값/응답 원문을 failure_reason이나 로그에 복사하지 않는다.
                    throw new InvalidOperationException("AI Server Chat Translation Error");
                }

                await Task.Delay(options.RetryWait, cancellationToken);
            }
        }

        // 원본 empty/blank 검증은 ExternalApiClient의 Retry/CB 바깥에 있다.
        string normalized = translated is null ? "" : ChatTranslationExecutionPolicy.Normalize(
            System.Text.Json.JsonSerializer.SerializeToElement(translated));
        if (ChatMessageText.IsBlank(normalized))
        {
            throw new InvalidOperationException("AI translation response is empty.");
        }

        return ChatMessageText.Trim(normalized);
    }

    private async Task<string?> SendOnceAsync(ChatModelExecutionCommand command, CancellationToken cancellationToken)
    {
        using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptTimeout.CancelAfter(options.AttemptTimeout);
        // 번역 전용 prompt와 결과 정제는 CHAT 정책이 소유한다.
        ChatModelExecutionResult result = await ChatModelExecutionTransport.ExecuteAsync(
            client, options.AiBaseUri!, options.ApiKey!, command, attemptTimeout.Token);
        if (result.Output.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            throw new System.Text.Json.JsonException("Invalid translation execution output.");
        }

        return result.Output.GetString();
    }
}
