using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.Core;
using TranslaCat.Chat.Application.OpenRooms;
using TranslaCat.Chat.Application.ProfileImages;

namespace TranslaCat.Chat.Infrastructure.Core;

public sealed class HttpChatCoreStorage(HttpChatCoreClient client) : IOpenProfileStorage, IChatAiProfileStorage, IChatProfileImageObjectStore
{
    public async Task<string?> ResolveUrlAsync(string objectKey, CancellationToken token)
    {
        var response = await client.UrlsAsync([objectKey], token);

        // 다른 key의 URL이나 null 항목을 현재 이미지의 주소로 채택하지 않는다.
        if (response.Objects is not { Count: 1 }
            || response.Objects[0] is null
            || response.Objects[0].ObjectKey != objectKey)
        {
            throw new ChatCoreUnavailableException();
        }

        return response.Objects[0].Url;
    }

    public Task StoreAsync(string objectKey, string contentType, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        return client.StoreAsync(objectKey, contentType, bytes, token);
    }

    public Task DeleteAsync(string objectKey, CancellationToken token)
    {
        return client.DeleteAsync(objectKey, token);
    }
}
