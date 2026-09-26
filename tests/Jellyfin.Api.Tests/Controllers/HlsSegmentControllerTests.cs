using System;
using System.IO;
using System.Security.Claims;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Controllers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

// Private output must belong to the requested item and authenticated job owner.
// Filenames and playback-session IDs alone confer no access.
public sealed class HlsSegmentControllerTests : IDisposable
{
    private readonly Mock<IServerConfigurationManager> _config = new();
    private readonly Mock<ITranscodeManager> _transcodeManager = new();
    private readonly string _transcodePath;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _itemId = Guid.NewGuid();
    private readonly string _playlistId = Guid.NewGuid().ToString("N");

    public HlsSegmentControllerTests()
    {
        _transcodePath = Directory.CreateTempSubdirectory("jellyfin-hls-segment-tests-").FullName;

        _config.Setup(c => c.GetConfiguration("encoding"))
            .Returns(new EncodingOptions { TranscodingTempPath = _transcodePath });
        _config.SetupGet(c => c.CommonApplicationPaths).Returns(Mock.Of<IApplicationPaths>());
    }

    private HlsSegmentController CreateController(string requestPath)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = requestPath;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(InternalClaimTypes.UserId, _userId.ToString()), new Claim(InternalClaimTypes.Token, "isolated-test-token")],
            "test"));

        return new HlsSegmentController(_config.Object, _transcodeManager.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    [Fact]
    public void GetHlsAudioSegmentLegacy_SegmentInsideTranscodePath_ReturnsFile()
    {
        var controller = CreateController("/Audio/abc/hls/segment/stream.mp3");
        using var job = OwnJob("segment.mp3", TranscodingJobType.Progressive);
        var result = controller.GetHlsAudioSegmentLegacy(_itemId.ToString(), "segment");

        Assert.IsType<PhysicalFileResult>(result);
    }

    [Theory]
    [InlineData("../../../../etc/passwd")]
    [InlineData("subdir/../../../../etc/passwd")]
    public void GetHlsAudioSegmentLegacy_TraversalOutsideTranscodePath_ReturnsBadRequest(string segmentId)
    {
        var controller = CreateController("/Audio/abc/hls/segment/stream.mp3");

        var result = controller.GetHlsAudioSegmentLegacy("abc", segmentId);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetHlsAudioSegmentLegacy_AbsoluteRootedPath_ReturnsBadRequest()
    {
        var controller = CreateController("/Audio/abc/hls/segment/stream.mp3");

        // A rooted segment id makes Path.GetFullPath discard the transcode base.
        var rooted = OperatingSystem.IsWindows() ? "C:\\Windows\\win.ini" : "/etc/passwd";
        var result = controller.GetHlsAudioSegmentLegacy("abc", rooted);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetHlsAudioSegmentLegacy_SiblingPrefixDirectory_ReturnsBadRequest()
    {
        var controller = CreateController("/Audio/abc/hls/segment/stream.mp3");

        // Resolves to "<transcodePath>-evil/passwd", which shares the transcode path as a string prefix.
        var result = controller.GetHlsAudioSegmentLegacy("abc", "../jellyfin-hls-segment-tests-evil/passwd");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetHlsPlaylistLegacy_M3u8InsideTranscodePath_ReturnsFile()
    {
        var controller = CreateController("/Videos/abc/hls/list/stream.m3u8");
        using var job = OwnJob(_playlistId + ".m3u8", TranscodingJobType.Hls);
        File.WriteAllText(job.Path!, "#EXTM3U\n" + _playlistId + "0.ts\n");
        var result = controller.GetHlsPlaylistLegacy(_itemId.ToString(), _playlistId);
        Assert.Contains(_playlistId + "0.ts?ApiKey=isolated-test-token", Assert.IsType<ContentResult>(result).Content, StringComparison.Ordinal);
    }

    [Fact]
    public void GetHlsPlaylistLegacy_NonPlaylistExtension_ReturnsBadRequest()
    {
        // Playlist endpoint serves only .m3u8, even for a path inside the transcode dir.
        var controller = CreateController("/Videos/abc/hls/list/stream.mp4");

        var result = controller.GetHlsPlaylistLegacy("abc", "list");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Theory]
    [InlineData("../../../../etc/passwd")]
    public void GetHlsPlaylistLegacy_TraversalOutsideTranscodePath_ReturnsBadRequest(string playlistId)
    {
        var controller = CreateController("/Videos/abc/hls/list/stream.m3u8");

        var result = controller.GetHlsPlaylistLegacy("abc", playlistId);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void GetHlsVideoSegmentLegacy_SegmentInsideTranscodePath_ReturnsFile()
    {
        using var job = OwnJob(_playlistId + ".m3u8", TranscodingJobType.Hls);
        var controller = CreateController("/Videos/abc/hls/playlist123/seg1.ts");
        var result = controller.GetHlsVideoSegmentLegacy(_itemId.ToString(), _playlistId, _playlistId + "0", "ts");

        Assert.IsType<PhysicalFileResult>(result);
    }

    [Fact]
    public void GetHlsVideoSegmentLegacy_NoMatchingPlaylist_ReturnsNotFound()
    {
        var controller = CreateController("/Videos/abc/hls/playlist123/seg1.ts");

        var result = controller.GetHlsVideoSegmentLegacy(_itemId.ToString(), _playlistId, _playlistId + "0", "ts");

        Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData("../../../../etc/passwd")]
    public void GetHlsVideoSegmentLegacy_TraversalOutsideTranscodePath_ReturnsBadRequest(string segmentId)
    {
        var controller = CreateController("/Videos/abc/hls/playlist123/seg1.ts");

        var result = controller.GetHlsVideoSegmentLegacy("abc", "playlist123", segmentId, "ts");

        Assert.IsType<BadRequestObjectResult>(result);
        _transcodeManager.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExistingOutput_CannotBeReadForAnotherOwnerOrItem(bool wrongOwner)
    {
        using var job = OwnJob(_playlistId + ".m3u8", TranscodingJobType.Hls);
        if (wrongOwner)
        {
            job.UserId = Guid.NewGuid();
        }
        else
        {
            job.ItemId = Guid.NewGuid();
        }

        var controller = CreateController("/Videos/abc/hls/list/segment.ts");
        Assert.IsType<NotFoundResult>(controller.GetHlsVideoSegmentLegacy(_itemId.ToString(), _playlistId, _playlistId + "0", "ts"));
        _transcodeManager.Verify(value => value.OnTranscodeBeginRequest(It.IsAny<string>(), It.IsAny<TranscodingJobType>()), Times.Never);
    }

    [Fact]
    public void SegmentMustBelongToTheExactAuthorizedPlaylist()
    {
        using var job = OwnJob(_playlistId + ".m3u8", TranscodingJobType.Hls);
        var controller = CreateController("/Videos/abc/hls/list/segment.ts");
        Assert.IsType<BadRequestObjectResult>(controller.GetHlsVideoSegmentLegacy(_itemId.ToString(), _playlistId, Guid.NewGuid().ToString("N") + "0", "ts"));
        _transcodeManager.VerifyNoOtherCalls();
    }

    public void Dispose() => Directory.Delete(_transcodePath, true);

    private TranscodingJob OwnJob(string filename, TranscodingJobType type)
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            Path = Path.Combine(_transcodePath, filename), Type = type, ItemId = _itemId, UserId = _userId,
            MediaSource = new MediaSourceInfo { Id = _itemId.ToString("N") }
        };
        _transcodeManager.Setup(value => value.GetTranscodingJob(job.Path, type)).Returns(job);
        _transcodeManager.Setup(value => value.OnTranscodeBeginRequest(job.Path, type)).Returns(job);
        return job;
    }
}
