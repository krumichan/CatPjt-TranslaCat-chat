using TranslaCat.Chat.Application.ProfileImages;

namespace TranslaCat.Chat.UnitTests;

public sealed class ChatProfileImagePolicyTests
{
    [Theory]
    [InlineData("jpg", "IMAGE/JPEG; charset=binary", "image/jpeg")]
    [InlineData("png", "application/octet-stream", "image/png")]
    [InlineData("webp", null, "image/webp")]
    public void Signature_and_declared_type_use_source_policy_without_decoding_image(string extension, string? declared, string detected)
    {
        // 준비: 원본처럼 signature만 있는 합성 bytes도 검사에 통과한다. decode 검증으로 보고하지 않는다.
        byte[] bytes = extension switch
        {
            "jpg" => [0xff, 0xd8, 0xff],
            "png" => [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a],
            _ => "RIFF0000WEBP"u8.ToArray()
        };

        // 실행
        var result = ChatProfileImagePolicy.Validate(new(declared, bytes), ChatProfileImageKind.Profile);

        // 검증
        Assert.Equal(detected, result.ContentType);
        Assert.Equal(extension, result.Extension);
        Assert.Same(bytes, result.Bytes);
    }

    [Theory]
    [InlineData("empty", "PROFILE_IMAGE_FILE_REQUIRED")]
    [InlineData("invalid", "PROFILE_IMAGE_INVALID_BINARY")]
    [InlineData("mismatch", "PROFILE_IMAGE_CONTENT_TYPE_MISMATCH")]
    [InlineData("unsupported", "PROFILE_IMAGE_UNSUPPORTED_CONTENT_TYPE")]
    public void Validation_rejections_preserve_source_error_codes(string scenario, string code)
    {
        // 준비
        var bytes = scenario == "empty" ? Array.Empty<byte>() : scenario == "invalid" ? new byte[] { 1, 2, 3 } : [0xff, 0xd8, 0xff];
        var declared = scenario == "mismatch" ? "image/png" : scenario == "unsupported" ? "image/gif" : "image/jpeg";

        // 실행 / 검증
        var error = Assert.Throws<ChatProfileImageException>(() => ChatProfileImagePolicy.Validate(new(declared, bytes), ChatProfileImageKind.Profile));
        Assert.Equal(code, error.Code);
    }

    [Theory]
    [InlineData(ChatProfileImageKind.Profile, 5)]
    [InlineData(ChatProfileImageKind.Background, 10)]
    public void Size_limit_is_inclusive_and_checks_binary_length(ChatProfileImageKind kind, int mebibytes)
    {
        // 준비
        var bytes = new byte[(mebibytes * 1024 * 1024) + 1];
        bytes[0] = 0xff;
        bytes[1] = 0xd8;
        bytes[2] = 0xff;

        // 실행 / 검증
        Assert.Equal("PROFILE_IMAGE_FILE_TOO_LARGE", Assert.Throws<ChatProfileImageException>(() => ChatProfileImagePolicy.Validate(new(null, bytes), kind)).Code);
        Assert.Equal("jpg", ChatProfileImagePolicy.Validate(new(null, bytes[..^1]), kind).Extension);
    }
}
