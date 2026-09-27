using System.Globalization;
using System.Text;

namespace TranslaCat.Chat.Api.Realtime;

public sealed record ChatStompFrame(string Command, IReadOnlyDictionary<string, string> Headers, byte[] Body);

public sealed class ChatStompProtocolException : Exception
{
    public ChatStompProtocolException() : base("Invalid STOMP frame.") { }
}

public sealed class ChatStompDecoder
{
    public const int MaximumFrameBytes = 65536;
    private const int MaximumHeaderBytes = 8192;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly List<byte> pending = [];

    public IReadOnlyList<ChatStompFrame> Append(ReadOnlySpan<byte> bytes)
    {
        // WebSocket 메시지/fragment 경계와 STOMP 프레임 경계는 서로 독립적이다.
        var frames = new List<ChatStompFrame>();
        foreach (var value in bytes)
        {
            pending.Add(value);
            if (pending.Count > MaximumFrameBytes)
            {
                throw new ChatStompProtocolException();
            }

            if (value == 0 || (pending.Count == 1 && value is 10 or 13))
            {
                while (TryRead(out var frame))
                {
                    if (frame is not null)
                    {
                        frames.Add(frame);
                    }
                }
            }
        }

        // 아직 body가 도착하지 않았어도 과도한 header는 즉시 거부한다.
        if (pending.Count > MaximumHeaderBytes && FindHeaderEnd(pending.ToArray()) < 0)
        {
            throw new ChatStompProtocolException();
        }
        return frames;
    }

    private bool TryRead(out ChatStompFrame? frame)
    {
        frame = null;
        if (pending.Count == 0)
        {
            return false;
        }
        if (pending[0] is 10 or 13)
        {
            pending.RemoveAt(0);
            return true;
        }

        var buffer = pending.ToArray();
        var headerEnd = FindHeaderEnd(buffer);
        if (headerEnd < 0)
        {
            return false;
        }
        if (headerEnd > MaximumHeaderBytes)
        {
            throw new ChatStompProtocolException();
        }

        string headerText;
        try
        {
            headerText = Utf8.GetString(buffer, 0, headerEnd);
        }
        catch (DecoderFallbackException)
        {
            throw new ChatStompProtocolException();
        }

        var lines = headerText.Split('\n');
        var command = lines[0].TrimEnd('\r');
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0 || headers.Count >= 64)
            {
                throw new ChatStompProtocolException();
            }

            var name = line[..colon];
            var value = line[(colon + 1)..];
            if (command is not ("CONNECT" or "STOMP" or "CONNECTED"))
            {
                name = Unescape(name);
                value = Unescape(value);
            }
            // STOMP의 첫 header 우선 규칙을 유지한다.
            headers.TryAdd(name, value);
        }

        var bodyStart = headerEnd;
        int bodyEnd;
        if (headers.TryGetValue("content-length", out var lengthText))
        {
            if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                || length < 0 || length > MaximumFrameBytes - bodyStart - 1)
            {
                throw new ChatStompProtocolException();
            }
            bodyEnd = bodyStart + length;
            if (buffer.Length <= bodyEnd)
            {
                return false;
            }
            if (buffer[bodyEnd] != 0)
            {
                throw new ChatStompProtocolException();
            }
        }
        else
        {
            bodyEnd = Array.IndexOf(buffer, (byte)0, bodyStart);
            if (bodyEnd < 0)
            {
                return false;
            }
        }

        frame = new ChatStompFrame(command, headers, buffer[bodyStart..bodyEnd]);
        pending.RemoveRange(0, bodyEnd + 1);
        return true;
    }

    private static int FindHeaderEnd(byte[] bytes)
    {
        for (var i = 1; i < bytes.Length; i++)
        {
            if (bytes[i] == 10 && bytes[i - 1] == 10)
            {
                return i + 1;
            }
            if (i >= 3 && bytes[i] == 10 && bytes[i - 1] == 13 && bytes[i - 2] == 10 && bytes[i - 3] == 13)
            {
                return i + 1;
            }
        }
        return -1;
    }

    private static string Unescape(string input)
    {
        var result = new StringBuilder();
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] != '\\')
            {
                result.Append(input[i]);
                continue;
            }
            if (++i == input.Length)
            {
                throw new ChatStompProtocolException();
            }
            result.Append(input[i] switch
            {
                'n' => '\n',
                'r' => '\r',
                'c' => ':',
                '\\' => '\\',
                _ => throw new ChatStompProtocolException()
            });
        }
        return result.ToString();
    }
}

public static class ChatStompEncoder
{
    public static byte[] Encode(string command, IReadOnlyDictionary<string, string>? headers = null, string body = "")
    {
        var buffer = new StringBuilder(command).Append('\n');
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                buffer.Append(Escape(name)).Append(':').Append(command == "CONNECTED" ? value : Escape(value)).Append('\n');
            }
        }
        buffer.Append("content-length:").Append(Encoding.UTF8.GetByteCount(body)).Append("\n\n").Append(body).Append('\0');
        return Encoding.UTF8.GetBytes(buffer.ToString());
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace(":", "\\c");
    }
}
