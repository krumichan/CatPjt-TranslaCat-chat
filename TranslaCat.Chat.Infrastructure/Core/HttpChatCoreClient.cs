using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TranslaCat.Chat.Application.Core;

namespace TranslaCat.Chat.Infrastructure.Core;

public sealed class HttpChatCoreClient(HttpClient client, ChatCoreHttpSettings settings, IChatCoreAccessTokenProvider tokens)
{
    public const int MaximumResponseBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12
    };

    public Task<ChatCoreAccounts> AccountsAsync(long[] userIds, string[] publicIds, CancellationToken token)
    {
        return JsonAsync<ChatCoreAccounts>("/internal/v1/chat/accounts/lookup", "chat:accounts:read", new
        {
            userIds,
            publicIds
        }, token);
    }

    public Task<ChatCoreRelations> RelationsAsync(long requesterUserId, long[] targetUserIds, CancellationToken token)
    {
        return JsonAsync<ChatCoreRelations>("/internal/v1/chat/relations/query", "chat:relations:read", new
        {
            requesterUserId,
            targetUserIds
        }, token);
    }

    public Task<ChatCoreObjectUrls> UrlsAsync(string[] objectKeys, CancellationToken token)
    {
        return JsonAsync<ChatCoreObjectUrls>("/internal/v1/chat/storage/urls", "chat:storage:read", new
        {
            objectKeys
        }, token);
    }

    public async Task StoreAsync(string key, string contentType, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        // header로 전송하기 전에 제어 문자와 지원하지 않는 MIME을 거절한다.
        if (string.IsNullOrWhiteSpace(key) || key.Length > 500 || key.Any(char.IsControl)
            || contentType is not ("image/jpeg" or "image/png" or "image/webp") || bytes.IsEmpty)
        {
            throw new ChatCoreUnavailableException();
        }

        using var content = new ReadOnlyMemoryContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var request = Request(HttpMethod.Put, "/internal/v1/chat/storage/objects", "chat:storage:write", content);
        if (!request.Headers.TryAddWithoutValidation("X-Chat-Object-Key", key))
        {
            throw new ChatCoreUnavailableException();
        }

        await EmptyAsync(request, token);
    }

    public async Task DeleteAsync(string objectKey, CancellationToken token)
    {
        using var request = Request(HttpMethod.Delete, "/internal/v1/chat/storage/objects", "chat:storage:delete", JsonContent.Create(new
        {
            objectKey
        }));
        await EmptyAsync(request, token);
    }

    private async Task<T> JsonAsync<T>(string path, string scope, object body, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, path, scope, JsonContent.Create(body));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(settings.Timeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                throw new ChatCoreUnavailableException();
            }

            // 응답의 실제 byte도 제한한다. remote 오류·계정 값·토큰을 예외/로그에 포함하지 않는다.
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            while (true)
            {
                int count = await source.ReadAsync(chunk, deadline.Token);
                if (count == 0)
                {
                    break;
                }

                if (buffer.Length + count > MaximumResponseBytes)
                {
                    throw new ChatCoreUnavailableException();
                }

                buffer.Write(chunk, 0, count);
            }
            using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 12 });
            RejectDuplicateFields(json.RootElement);
            return json.RootElement.Deserialize<T>(Json) ?? throw new ChatCoreUnavailableException();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            throw new ChatCoreUnavailableException();
        }
    }

    private async Task EmptyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(settings.Timeout);
        try
        {
            // Store/Delete는 재시도하지 않는다. 응답 유실과 object commit을 동등하게 취급하지 않는다.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.NoContent)
            {
                throw new ChatCoreUnavailableException();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            throw new ChatCoreUnavailableException();
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string scope, HttpContent content)
    {
        var request = new HttpRequestMessage(method, new Uri(settings.BaseUri, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.CreateToken(scope));
        return request;
    }

    private static void RejectDuplicateFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in element.EnumerateObject())
            {
                if (!names.Add(field.Name))
                {
                    throw new JsonException();
                }

                RejectDuplicateFields(field.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in element.EnumerateArray())
            {
                RejectDuplicateFields(value);
            }
        }
    }
}
