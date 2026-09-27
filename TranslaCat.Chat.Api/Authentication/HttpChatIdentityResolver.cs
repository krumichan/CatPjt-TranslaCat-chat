using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TranslaCat.Chat.Api.ServiceAuthentication;

namespace TranslaCat.Chat.Api.Authentication;

public sealed class HttpChatIdentityResolver(HttpClient client, IOptions<ChatIdentityOptions> options,
    IHostEnvironment environment, TimeProvider timeProvider) : IChatIdentityResolver
{
    public const string IdentityPath = "/internal/v1/chat/identity";
    public const int MaximumResponseBytes = 16 * 1024;

    public async Task<ChatResolvedIdentity?> ResolveAsync(ChatIdentityLookup lookup, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = options.Value;
        if (!settings.Enabled || !TryOrigin(settings.BaseUrl, environment.IsDevelopment(), out var origin)
            || settings.TimeoutSeconds is < 1 or > 30 || !ChatServiceJwt.IsConfigured(settings.ServiceAuthentication)
            || lookup.TokenUserId <= 0 || string.IsNullOrWhiteSpace(lookup.Subject) || lookup.Subject.Length > 320)
        {
            throw new ChatIdentityUnavailableException();
        }

        // 현재 사용자 조회 전용 내부 경로다. public profile GET이나 Chat proxy로 우회하지 않는다.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, IdentityPath))
        {
            Content = JsonContent.Create(new { subject = lookup.Subject, tokenUserId = lookup.TokenUserId })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ChatServiceJwt.Issue(
            settings.ServiceAuthentication, environment.EnvironmentName, "chat-identity", "chat:identity:read",
            lookup.TokenUserId, timeProvider.GetUtcNow()));

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                throw new ChatIdentityUnavailableException();
            }

            // Content-Length가 없거나 거짓이어도 작은 응답 상한을 적용한다. body/토큰/계정값은 오류에 담지 않는다.
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[2048];
            while (true)
            {
                var read = await source.ReadAsync(buffer, deadline.Token);
                if (read == 0)
                {
                    break;
                }

                if (body.Length + read > MaximumResponseBytes)
                {
                    throw new ChatIdentityUnavailableException();
                }

                body.Write(buffer, 0, read);
            }
            using var json = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            return ParseIdentity(json.RootElement, lookup);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
            or InvalidOperationException or OperationCanceledException)
        {
            throw new ChatIdentityUnavailableException();
        }
    }

    private static ChatResolvedIdentity ParseIdentity(JsonElement value, ChatIdentityLookup lookup)
    {
        // 필수 nullable/boolean을 기본값으로 보완하지 않는다. ID와 email은 요청의 검증된 원본과 정확히 일치해야 한다.
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ChatIdentityUnavailableException();
        }

        var fields = value.EnumerateObject().ToArray();
        if (fields.Length != 4 || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != 4
            || !value.TryGetProperty("userId", out var id) || !id.TryGetInt64(out var userId) || userId != lookup.TokenUserId
            || !value.TryGetProperty("email", out var email) || email.ValueKind != JsonValueKind.String
            || !string.Equals(email.GetString(), lookup.Subject, StringComparison.Ordinal)
            || !value.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String
            || role.GetString() is not ("ROLE_USER" or "ROLE_ADMIN")
            || !value.TryGetProperty("canAuthenticate", out var canAuthenticate)
            || canAuthenticate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ChatIdentityUnavailableException();
        }
        return new(userId, email.GetString()!, role.GetString()!, canAuthenticate.GetBoolean());
    }

    private static bool TryOrigin(string? value, bool development, out Uri origin)
    {
        origin = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || string.IsNullOrWhiteSpace(parsed.Host)
            || parsed.UserInfo.Length != 0 || parsed.Query.Length != 0 || parsed.Fragment.Length != 0
            || parsed.AbsolutePath != "/")
        {
            return false;
        }

        // 환경 밖의 plain HTTP, 설정에 섞인 경로/사용자정보/쿼리를 거부해 credential의 다른 대상 전송을 막는다.
        var loopback = parsed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(parsed.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
        if (parsed.Scheme != Uri.UriSchemeHttps && !(development && loopback && parsed.Scheme == Uri.UriSchemeHttp))
        {
            return false;
        }

        origin = parsed;
        return true;
    }
}

public sealed class ChatIdentityUnavailableException() : Exception("Chat identity service is unavailable.");
