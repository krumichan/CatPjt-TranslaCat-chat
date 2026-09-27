using System.Text;

namespace TranslaCat.Chat.Infrastructure.Configuration;

public static class ChatSecretFile
{
    public const int MaximumBytes = 16 * 1024;

    public static string? Resolve(string? value, string? file, string settingName)
    {
        // 값과 파일을 함께 받으면 우선순위를 추측하지 않는다. 오류에는 내용/경로를 넣지 않는다.
        if (string.IsNullOrWhiteSpace(file))
        {
            return value;
        }
        if (!string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Conflicting value and secret file for {settingName}.");
        }

        try
        {
            if (!Path.IsPathFullyQualified(file))
            {
                throw new IOException();
            }

            // 고정 크기 buffer로 읽어 파일 크기와 관계없이 입력 상한을 적용한다.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[MaximumBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var count = stream.Read(bytes, length, bytes.Length - length);
                if (count == 0)
                {
                    break;
                }

                length += count;
            }
            if (length == 0 || length > MaximumBytes)
            {
                throw new IOException();
            }

            var secret = new UTF8Encoding(false, true).GetString(bytes, 0, length).TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(secret) || secret.Any(char.IsControl) || secret[0] == '\uFEFF')
            {
                throw new IOException();
            }
            return secret;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException($"Cannot read secret file for {settingName}.");
        }
    }
}
