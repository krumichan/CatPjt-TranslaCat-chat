using System.Text;
using TranslaCat.Chat.Api.Realtime;

namespace TranslaCat.Chat.ApiTests.Realtime;

public sealed class ChatStompCodecTests
{
    [Fact]
    public void Fragmented_multibyte_body_embedded_null_and_following_frame_preserve_byte_boundaries()
    {
        // 준비: UTF-8 byte 수는 C# 문자열 길이와 다르고 body에는 NUL도 허용된다.
        var decoder = new ChatStompDecoder();
        var bytes = Encoding.UTF8.GetBytes("SEND\r\ndestination:/app/chat/rooms/41/messages\r\ncontent-length:5\r\n\r\n한\0x\0\nDISCONNECT\nreceipt:end\n\n\0");
        var frames = new List<ChatStompFrame>();

        // 실행: WebSocket fragment보다 더 작은 한 byte 단위로 입력한다.
        foreach (var value in bytes)
        {
            frames.AddRange(decoder.Append([value]));
        }

        // 검증
        Assert.Equal(2, frames.Count);
        Assert.Equal("SEND", frames[0].Command);
        Assert.Equal("한\0x", Encoding.UTF8.GetString(frames[0].Body));
        Assert.Equal("DISCONNECT", frames[1].Command);
        Assert.Equal("end", frames[1].Headers["receipt"]);
    }

    [Fact]
    public void Escaping_and_first_repeated_header_follow_STOMP_rules()
    {
        // 준비
        var decoder = new ChatStompDecoder();
        var bytes = Encoding.UTF8.GetBytes("SUBSCRIBE\nid:a\\cb\\nc\\rd\\\\e\nid:ignored\ndestination:/user/queue/chat/read\n\n\0");

        // 실행
        var frame = Assert.Single(decoder.Append(bytes));

        // 검증
        Assert.Equal("a:b\nc\rd\\e", frame.Headers["id"]);
        var encoded = Encoding.UTF8.GetString(ChatStompEncoder.Encode("RECEIPT", new Dictionary<string, string> { ["receipt-id"] = frame.Headers["id"] }));
        Assert.Contains("receipt-id:a\\cb\\nc\\rd\\\\e\n", encoded);
    }

    [Theory]
    [InlineData("SEND\ncontent-length:-1\n\n\0")]
    [InlineData("SEND\ncontent-length:999999\n\n\0")]
    [InlineData("SEND\ncontent-length:1\n\nxx\0")]
    [InlineData("SUBSCRIBE\nid:a\\t\n\n\0")]
    [InlineData("SEND\nmissing-colon\n\n\0")]
    public void Malformed_length_header_or_escape_is_rejected(string input)
    {
        // 준비
        var decoder = new ChatStompDecoder();

        // 실행 / 검증
        Assert.Throws<ChatStompProtocolException>(() => decoder.Append(Encoding.UTF8.GetBytes(input)));
    }

    [Fact]
    public void Frame_limit_rejects_unterminated_large_body()
    {
        // 준비
        var decoder = new ChatStompDecoder();
        var bytes = Encoding.UTF8.GetBytes("SEND\n\n" + new string('x', ChatStompDecoder.MaximumFrameBytes));

        // 실행 / 검증
        Assert.Throws<ChatStompProtocolException>(() => decoder.Append(bytes));
    }
}
