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

    public async Task<ChatCorePushTargets> PushTargetsAsync(
        long[] userIds, string appEnvironment, string projectId, CancellationToken token)
    {
        // 내부 registry는 public 응답 envelope가 없는 별도 계약이다. 요청/응답 경계를 모두 제한한다.
        if (userIds.Length is < 1 or > 100 || userIds.Any(id => id <= 0) || userIds.Distinct().Count() != userIds.Length
            || appEnvironment is not ("development" or "preview" or "production")
            || !Guid.TryParseExact(projectId, "D", out var project) || project.ToString("D") != projectId)
        {
            throw new ChatCoreUnavailableException();
        }

        var result = await JsonAsync<ChatCorePushTargets>("/internal/v1/chat/push-targets/query", "chat:push-targets:read",
            new { userIds, appEnvironment, projectId }, token);
        if (result.Targets is null || result.Targets.Count > userIds.Length * 10
            || result.Targets.Any(target => target is null || !userIds.Contains(target.UserId) || !ValidPushTarget(target))
            || result.Targets.Select(target => target.InstallationId).Distinct(StringComparer.Ordinal).Count() != result.Targets.Count)
        {
            throw new ChatCoreUnavailableException();
        }

        return result;
    }

    public async Task InvalidatePushAsync(string installationId, string accountBindingId, long generation, long tokenVersion, CancellationToken token)
    {
        if (!Guid.TryParseExact(installationId, "D", out var installation) || installation.ToString("D") != installationId
            || accountBindingId is not { Length: >= 1 and <= 128 }
            || !accountBindingId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            || generation is <= 0 or > 9007199254740991 || tokenVersion is <= 0 or > 9007199254740991)
            throw new ChatCoreUnavailableException();

        // receipt에 기록된 네 값 전체를 전달한다. 갱신된 설치를 지우는 광범위 revoke는 하지 않는다.
        using var request = Request(HttpMethod.Post, "/internal/v1/chat/push-installations/invalidate",
            "chat:push-installations:invalidate", JsonContent.Create(new
            {
                installationId, accountBindingId, generation, tokenVersion
            }));
        await EmptyAsync(request, token);
    }

    private static bool ValidPushTarget(ChatCorePushTarget target)
    {
        return target.UserId > 0 && Guid.TryParseExact(target.InstallationId, "D", out var installation)
            && installation.ToString("D") == target.InstallationId
            && target.AccountBindingId is { Length: >= 1 and <= 128 }
            && target.AccountBindingId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            && target.Generation is > 0 and <= 9007199254740991
            && target.TokenVersion is > 0 and <= 9007199254740991
            && target.Platform is "android" or "ios"
            && target.ExpoPushToken is { Length: >= 1 and <= 275 }
            && System.Text.RegularExpressions.Regex.IsMatch(target.ExpoPushToken,
                @"\A(?:ExpoPushToken|ExponentPushToken)\[[A-Za-z0-9_-]{1,256}\]\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
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
