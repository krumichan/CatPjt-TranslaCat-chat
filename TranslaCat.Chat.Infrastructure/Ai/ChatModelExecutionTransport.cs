using System.Net.Http.Json;
using System.Text.Json;
using TranslaCat.Chat.Application.Ai;

namespace TranslaCat.Chat.Infrastructure.Ai;

internal static class ChatModelExecutionTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SafeFailureCodes =
    [
        "MODEL_EXECUTION_UNAUTHORIZED", "EXECUTION_REQUEST_INVALID", "EXECUTION_REQUEST_TOO_LARGE",
        "EXECUTION_SCHEMA_INVALID", "PROVIDER_TIMEOUT", "PROVIDER_UNAVAILABLE",
        "PROVIDER_CONFIGURATION_ERROR", "PROVIDER_EXECUTION_FAILED", "REFUSAL",
        "OUTPUT_TOKEN_LIMIT", "RESPONSE_INCOMPLETE", "EMPTY_OUTPUT", "JSON_INVALID"
    ];

    public static async Task<ChatModelExecutionResult> ExecuteAsync(
        HttpClient client, Uri baseUri, string apiKey, ChatModelExecutionCommand command,
        CancellationToken cancellationToken)
    {
        // 한 HTTP 시도는 Provider 호출 하나만 허용한다. 자격증명은 body나 오류에 넣지 않는다.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/internal/v1/model/execute"));
        request.Headers.Add("X-API-KEY", apiKey);
        request.Content = JsonContent.Create(command, options: Json);

        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await ClassifyFailureAsync(response, cancellationToken);
        }

        // 실행기의 기술 메타데이터는 DTO로 받고, 업무 output은 Application 정책에 그대로 넘긴다.
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<ChatModelExecutionResult>(body, Json, cancellationToken);
        if (result is null || result.ProviderCalls != 1)
        {
            throw new JsonException("Invalid model execution response.");
        }

        return result;
    }

    private static async Task<ChatModelExecutionFailure> ClassifyFailureAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // AI의 고정 기술 코드를 안전한 목록으로 제한하고 응답 본문은 진단에 노출하지 않는다.
        int status = (int)response.StatusCode;
        bool retryable = status is 408 or 429 or >= 500;
        string code = status switch
        {
            401 or 403 => "MODEL_EXECUTION_UNAUTHORIZED",
            413 => "EXECUTION_REQUEST_TOO_LARGE",
            422 => "EXECUTION_REQUEST_INVALID",
            429 or 503 => "PROVIDER_UNAVAILABLE",
            504 => "PROVIDER_TIMEOUT",
            _ => "MODEL_EXECUTION_HTTP_ERROR"
        };
        int? retryAfter = null;

        if (response.Content.Headers.ContentLength is null or <= 8192 &&
            response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            try
            {
                await response.Content.LoadIntoBufferAsync(8192, cancellationToken);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (body.RootElement.ValueKind == JsonValueKind.Object
                    && body.RootElement.TryGetProperty("detail", out var detail)
                    && detail.ValueKind == JsonValueKind.Object)
                {
                    if (detail.TryGetProperty("code", out var rawCode) && rawCode.ValueKind == JsonValueKind.String
                        && SafeFailureCodes.Contains(rawCode.GetString()!))
                    {
                        code = rawCode.GetString()!;
                    }

                    if (detail.TryGetProperty("retryable", out var rawRetryable)
                        && rawRetryable.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        retryable = rawRetryable.GetBoolean();
                    }

                    // AI의 하루 상한을 보존한다. 긴 유효값을 null로 바꾸면 조기 재시도된다.
                    if (detail.TryGetProperty("retryAfterSeconds", out var rawDelay)
                        && rawDelay.ValueKind == JsonValueKind.Number && rawDelay.TryGetInt32(out int delay)
                        && delay is >= 0 and <= 86_400)
                    {
                        retryAfter = delay;
                    }
                }
            }
            catch (JsonException)
            {
                // 형식 오류도 원문 없이 status 기반 코드만 사용한다.
            }
            catch (HttpRequestException)
            {
                // 크기 초과 등은 원문 없이 status 기반 코드만 사용한다.
            }
        }

        return new(response.StatusCode, code, retryable, retryAfter);
    }
}
