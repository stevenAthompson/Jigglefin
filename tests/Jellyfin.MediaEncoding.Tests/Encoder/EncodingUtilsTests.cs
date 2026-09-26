using System;
using System.IO;
using MediaBrowser.MediaEncoding.Encoder;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.Encoder;

public sealed class EncodingUtilsTests
{
    [Theory]
    [InlineData(@"W:\Media\Chapter 01.m4b")]
    [InlineData(@"W:\Media\音楽 'chapter'.m4b")]
    public void BindFileInput_ReplacesOnlyTheCompleteSelectedInput(string logical)
    {
        const string ReadPath = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\Media\Selected.m4b";
        var original = EncodingUtils.GetInputArgument("file", logical, MediaProtocol.File);
        var expected = EncodingUtils.GetInputArgument("file", ReadPath, MediaProtocol.File);
        var args = $"-v warning -i {original} -metadata title=\"{logical}\" -f wav \"output.wav\"";
        Assert.Equal($"-v warning -i {expected} -metadata title=\"{logical}\" -f wav \"output.wav\"", EncodingUtils.BindFileInput(args, logical, ReadPath));
        Assert.Equal("-i " + expected, EncodingUtils.BindFileInput("-i " + original, logical, ReadPath));
    }

    [Theory]
    [InlineData("-version")]
    [InlineData("-i file:\"W:\\other.mp4\"")]
    [InlineData("-i file:\"W:\\Selected.mp4\"suffix")]
    [InlineData("prefix-i file:\"W:\\Selected.mp4\"")]
    [InlineData("-i file:\"W:\\Selected.mp4\" -i file:\"W:\\Selected.mp4\"")]
    public void BindFileInput_FailsClosedOnUnexpectedOrAmbiguousCommands(string arguments)
        => Assert.Throws<InvalidDataException>(() => EncodingUtils.BindFileInput(arguments, @"W:\Selected.mp4", @"C:\Pinned\Selected.mp4"));
}
