using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TranslaCat.Chat.Infrastructure.Push;

public sealed record ExpoPushPayload(
    string To,
    string Title,
    string Body,
    string ChannelId,
    string Sound,
    ExpoPushData Data,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Badge = null);

public sealed record ExpoPushData(
    int SchemaVersion,
    string EventId,
    string RoomId,
    string MessageId,
    string AccountBindingId);

public sealed record ExpoOpenInvitationPushPayload(
    string To,
    string Title,
    string Body,
    string ChannelId,
    string Sound,
    ExpoOpenInvitationPushData Data,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Badge = null);

public sealed record ExpoOpenInvitationPushData(
    int SchemaVersion,
    string Kind,
    string EventId,
    string NotificationId,
    string RoomId,
    string AccountBindingId);

public sealed record ExpoPushResult(string Status, string? TicketId = null, string? Error = null);

public sealed class ExpoChatPushClient(HttpClient client, ChatPushOptions options)
{
    private static readonly Uri SendUri = new("https://exp.host/--/api/v2/push/send");
    private static readonly Uri ReceiptsUri = new("https://exp.host/--/api/v2/push/getReceipts");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumTicketIdLength = 128;

    public static string SerializePayload(ExpoPushPayload payload)
    {
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public Task<ExpoPushResult> SendAsync(ExpoPushPayload payload, CancellationToken cancellationToken)
    {
        // 한 수신자씩 전송하여 Expo의 배열 한도와 응답 순서의 대응을 고정한다.
        string body = "[" + SerializePayload(payload) + "]";
        return RequestAsync(SendUri, body, null, cancellationToken);
    }

    public static string SerializePayload(ExpoOpenInvitationPushPayload payload)
    {
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public Task<ExpoPushResult> SendAsync(ExpoOpenInvitationPushPayload payload, CancellationToken cancellationToken)
    {
        // 초대는 messageId를 위조하지 않는다. 기존 deadline/strict parser와 같은 전송 경로를 쓴다.
        string body = "[" + SerializePayload(payload) + "]";
        return RequestAsync(SendUri, body, null, cancellationToken);
    }

    public Task<ExpoPushResult> ReceiptAsync(string ticketId, CancellationToken cancellationToken)
    {
        if (!IsValidTicketId(ticketId))
        {
            return Task.FromResult(Failure("permanent"));
        }

        string body = JsonSerializer.Serialize(new { ids = new[] { ticketId } }, JsonOptions);
        return RequestAsync(ReceiptsUri, body, ticketId, cancellationToken);
    }

    private async Task<ExpoPushResult> RequestAsync(
        Uri uri,
        string body,
        string? receiptId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.RequestTimeoutSeconds <= 0 || options.MaxResponseBytes <= 0)
        {
            return Failure("permanent");
        }

        // 응답 헤더와 본문 읽기 모두 같은 deadline 안에 끝나야 한다.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            if (!string.IsNullOrEmpty(options.ExpoAccessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ExpoAccessToken);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                return Failure(response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500
                    ? "retry"
                    : "permanent");
            }

            // Content-Length가 없거나 거짓이어도 실제 읽은 byte 수로 상한을 지킨다.
            byte[]? bytes = await ReadBoundedAsync(response.Content, options.MaxResponseBytes, deadline.Token);
            if (bytes is null)
            {
                return Failure("invalid_response");
            }

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            JsonElement root = document.RootElement;
            if (!HasUniqueProperties(root)
                || root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out JsonElement data)
                || !HasValidEnvelope(root))
            {
                return Failure("invalid_response");
            }

            return receiptId is null ? ParseTicket(data) : ParseReceipt(data, receiptId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 공급자 예외의 메시지나 inner exception을 호출자에게 넘기지 않는다.
            throw new OperationCanceledException(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Failure("retry");
        }
        catch (HttpRequestException)
        {
            return Failure("retry");
        }
        catch (IOException)
        {
            return Failure("retry");
        }
        catch (JsonException)
        {
            return Failure("invalid_response");
        }
        catch (FormatException)
        {
            return Failure("permanent");
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > limit)
        {
            return null;
        }

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        byte[] buffer = new byte[Math.Min(limit, 8192)];
        while (true)
        {
            int count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + count > limit)
            {
                return null;
            }

            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    private static bool HasValidEnvelope(JsonElement root)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name == "errors")
            {
                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() != 0)
                {
                    return false;
                }
            }
            else if (property.Name != "data")
            {
                return false;
            }
        }

        return true;
    }

    private static ExpoPushResult ParseTicket(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != 1)
        {
            return Failure("invalid_response");
        }

        return ParseStatus(data[0], null);
    }

    private static ExpoPushResult ParseReceipt(JsonElement data, string receiptId)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return Failure("invalid_response");
        }

        // 조회한 ID 외의 응답을 수용하면 다른 전송의 결과와 혼동할 수 있다.
        foreach (JsonProperty property in data.EnumerateObject())
        {
            if (property.Name != receiptId)
            {
                return Failure("invalid_response");
            }
        }

        if (!data.TryGetProperty(receiptId, out JsonElement receipt))
        {
            return new ExpoPushResult("pending", receiptId);
        }

        return ParseStatus(receipt, receiptId);
    }

    private static ExpoPushResult ParseStatus(JsonElement value, string? receiptId)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("status", out JsonElement status)
            || status.ValueKind != JsonValueKind.String)
        {
            return Failure("invalid_response");
        }

        // 공급자 message는 보관하거나 반환하지 않고 알려진 오류 코드만 사용한다.
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (property.Name is not ("status" or "id" or "message" or "details")
                || (property.Name == "message" && property.Value.ValueKind != JsonValueKind.String)
                || (property.Name == "details" && property.Value.ValueKind != JsonValueKind.Object))
            {
                return Failure("invalid_response");
            }
        }

        if (status.GetString() == "ok")
        {
            if (value.TryGetProperty("message", out _) || value.TryGetProperty("details", out _))
            {
                return Failure("invalid_response");
            }

            if (receiptId is not null)
            {
                return value.TryGetProperty("id", out _) ? Failure("invalid_response") : new ExpoPushResult("ok", receiptId);
            }

            return value.TryGetProperty("id", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && IsValidTicketId(id.GetString())
                ? new ExpoPushResult("ok", id.GetString())
                : Failure("invalid_response");
        }

        if (status.GetString() != "error" || value.TryGetProperty("id", out _))
        {
            return Failure("invalid_response");
        }

        if (!value.TryGetProperty("details", out JsonElement details)
            || !details.TryGetProperty("error", out JsonElement error))
        {
            return Failure("permanent");
        }

        if (error.ValueKind != JsonValueKind.String)
        {
            return Failure("invalid_response");
        }

        return error.GetString() switch
        {
            "DeviceNotRegistered" => Failure("DeviceNotRegistered"),
            "MessageTooBig" => Failure("MessageTooBig"),
            "MessageRateExceeded" => Failure("MessageRateExceeded"),
            "MismatchSenderId" => Failure("MismatchSenderId"),
            "InvalidCredentials" => Failure("InvalidCredentials"),
            _ => Failure("permanent")
        };
    }

    private static bool HasUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || !HasUniqueProperties(property.Value))
                {
                    return false;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (!HasUniqueProperties(item))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsValidTicketId(string? value)
    {
        return !string.IsNullOrEmpty(value)
            && value.Length <= MaximumTicketIdLength
            && value.All(character => character is >= '!' and <= '~');
    }

    private static ExpoPushResult Failure(string error)
    {
        return new ExpoPushResult("error", Error: error);
    }
}
