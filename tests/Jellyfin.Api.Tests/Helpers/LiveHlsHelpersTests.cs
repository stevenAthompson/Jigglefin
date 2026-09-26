using System.IO;
using Jellyfin.Api.Helpers;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers;

public sealed class LiveHlsHelpersTests
{
    private const string Prefix = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedAssets_AreLocalAuthenticatedUrls(bool nested)
    {
        var input = $"#EXTM3U\r\n#EXT-X-MAP:URI=\"C:\\private\\{Prefix}-1.mp4\"\r\n#EXTINF:1,\r\nhls/{Prefix}/{Prefix}0.mp4\r\n";
        var result = HlsHelpers.AuthenticateLivePlaylist(input, Prefix, "token&+=", nested);
        var route = nested ? string.Empty : $"hls/{Prefix}/";
        Assert.Equal($"#EXTM3U\n#EXT-X-MAP:URI=\"{route}{Prefix}-1.mp4?ApiKey=token%26%2B%3D\"\n#EXTINF:1,\n{route}{Prefix}0.mp4?ApiKey=token%26%2B%3D\n", result);
    }

    [Theory]
    [InlineData("other0.ts")]
    [InlineData("0123456789abcdef0123456789abcdef-2.ts")]
    [InlineData("0123456789abcdef0123456789abcdef0.exe")]
    [InlineData("#EXT-X-KEY:METHOD=AES-128,URI=\"https://example.invalid/key\"")]
    [InlineData("#EXT-X-MAP:URI=\"unterminated")]
    public void UnexpectedReferences_AreRejected(string input)
        => Assert.Throws<InvalidDataException>(() => HlsHelpers.AuthenticateLivePlaylist(input, Prefix, "test"));
}
