using System.Globalization;
using TranslaCat.Chat.Application.AiManagement;
using TranslaCat.Chat.Application.Messaging;
using TranslaCat.Chat.Application.OpenRooms;

namespace TranslaCat.Chat.Application.ProfileImages;

public enum ChatProfileImageKind
{
    Profile, Background
}
public sealed record ChatProfileImageUpload(string? ContentType, byte[]? Bytes);
public sealed record ChatValidatedProfileImage(string ContentType, string Extension, byte[] Bytes);

public interface IChatProfileImageObjectStore
{
    // 실제 공통 storage adapter만 제공한다. 이 capability가 없는 기존 URL resolver는 업로드를 수행할 수 없다.
    Task StoreAsync(string objectKey, string contentType, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}

public interface IChatAiImageStore
{
    Task<ChatAiMember> UploadImageAsync(long userId, long roomId, long memberId, ChatProfileImageKind kind,
        ChatProfileImageUpload upload, DateTime now, CancellationToken cancellationToken);
    Task<ChatAiMember> DeleteImageAsync(long userId, long roomId, long memberId, ChatProfileImageKind kind,
        DateTime now, CancellationToken cancellationToken);
}

public interface IOpenProfileImageStore
{
    Task<OpenProfile> UploadImageAsync(long userId, long roomId, ChatProfileImageUpload upload, DateTime now, CancellationToken cancellationToken);
}

public sealed class ChatProfileImageException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}
public sealed class ChatProfileImageUnavailableException : Exception;

public static class ChatProfileImagePolicy
{
    public static ChatValidatedProfileImage Validate(ChatProfileImageUpload? upload, ChatProfileImageKind kind)
    {
        var bytes = upload?.Bytes;
        if (bytes is null || bytes.Length == 0)
        {
            throw Error("이미지 파일은 필수입니다.", "FILE_REQUIRED");
        }
        long maximum = (kind == ChatProfileImageKind.Profile ? 5L : 10L) * 1024 * 1024;
        if (bytes.LongLength > maximum)
        {
            throw Error("이미지 파일 용량이 허용 범위를 초과했습니다.", "FILE_TOO_LARGE");
        }

        // 원본 공통 validator의 signature 검사를 보존한다. 이미지 decode/재인코딩/악성 파일 검사는 아니다.
        var (contentType, extension) = Detect(bytes);
        if (!IsJavaBlank(upload!.ContentType))
        {
            var declared = ChatMessageText.Trim(upload.ContentType!.ToLowerInvariant().Split(';')[0]);
            if (declared is not ("image/jpeg" or "image/png" or "image/webp" or "application/octet-stream"))
            {
                throw Error("지원하지 않는 이미지 형식입니다. JPEG, PNG, WEBP만 사용할 수 있습니다.", "UNSUPPORTED_CONTENT_TYPE");
            }
            if (declared != "application/octet-stream" && declared != contentType)
            {
                throw Error("파일의 Content-Type과 실제 이미지 형식이 일치하지 않습니다.", "CONTENT_TYPE_MISMATCH");
            }
        }
        return new(contentType, extension, bytes);
    }

    public static string ObjectKey(long memberId, bool openProfile, ChatProfileImageKind kind, string extension)
    {
        return openProfile ? $"open-chat-profiles/{memberId.ToString(CultureInfo.InvariantCulture)}/{Guid.NewGuid():D}.{extension}"
                : $"chat-ai/{memberId.ToString(CultureInfo.InvariantCulture)}/{(kind == ChatProfileImageKind.Profile ? "profile" : "background")}/{Guid.NewGuid():D}.{extension}";
    }

    public static bool IsJavaBlank(string? value)
    {
        return value is null || value.All(character => character is (>= '\u0009' and <= '\u000d') or (>= '\u001c' and <= '\u001f')
                || (char.GetUnicodeCategory(character) is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                    && character is not ('\u00a0' or '\u2007' or '\u202f')));
    }

    private static (string Type, string Extension) Detect(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff)
        {
            return ("image/jpeg", "jpg");
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }))
        {
            return ("image/png", "png");
        }
        if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return ("image/webp", "webp");
        }
        throw Error("지원하지 않거나 손상된 이미지 파일입니다. JPEG, PNG, WEBP만 사용할 수 있습니다.", "INVALID_BINARY");
    }

    private static ChatProfileImageException Error(string message, string code)
    {
        return new(message, "PROFILE_IMAGE_" + code);
    }
}
