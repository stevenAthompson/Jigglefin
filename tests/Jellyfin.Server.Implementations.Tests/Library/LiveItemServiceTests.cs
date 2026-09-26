using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library.Live;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class LiveItemServiceTests : IDisposable
{
    private readonly DirectoryInfo _fixture = Directory.CreateTempSubdirectory("jigglefin-selected-test-");
    private readonly string _media;
    private readonly string _nfo;
    private readonly Mock<IMediaEncoder> _encoder = new(MockBehavior.Strict);
    private readonly LiveLibraryStore _library;
    private readonly LiveItemService _items;
    private readonly Guid _id;

    public LiveItemServiceTests()
    {
        var root = Directory.CreateDirectory(Path.Combine(_fixture.FullName, "Media"));
        _media = Path.Combine(root.FullName, "Chapter.mp3");
        _nfo = Path.Combine(root.FullName, "Chapter.nfo");
        File.WriteAllText(_media, "mock encoder fixture");
        File.WriteAllText(_nfo, "<movie><title>Local title</title><plot>Local plot</plot></movie>");
        var browser = new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader());
        _library = new LiveLibraryStore(browser, Path.Combine(_fixture.FullName, "State"));
        var library = _library.AddLibrary("Books", [root.FullName]);
        _id = _library.Browse(library.Id, TestContext.Current.CancellationToken).Single(item => item.Name == "Chapter.mp3").Id;
        _items = new LiveItemService(_library, browser, _encoder.Object, NullLogger<LiveItemService>.Instance);
    }

    [Fact]
    public void Resolve_OnlyUsesAttributesAndDoesNotReadLockedContent()
    {
        using var media = File.Open(_media, FileMode.Open, FileAccess.Read, FileShare.None);
        using var nfo = File.Open(_nfo, FileMode.Open, FileAccess.Read, FileShare.None);
        var item = Assert.IsAssignableFrom<BaseItem>(_items.Resolve(_id));
        Assert.Equal("Chapter.mp3", item.Name);
        Assert.Null(item.LiveContext.Source);
        Assert.Null(item.RunTimeTicks);
        _encoder.VerifyNoOtherCalls();
    }

    [Fact]
    public void SelectingMetadata_DoesNotProbeAndObservesEditsAndRemoval()
    {
        var item = _items.Resolve(_id)!;
        _items.LoadLocalMetadata(item);
        Assert.Equal("Local title", item.Name);
        Assert.Equal("Local plot", item.Overview);

        File.WriteAllText(_nfo, "<movie><title>A different title</title></movie>");
        _items.LoadLocalMetadata(item);
        Assert.Equal("A different title", item.Name);
        Assert.Null(item.Overview);
        File.Delete(_nfo);
        _items.LoadLocalMetadata(item);
        Assert.Equal("Chapter.mp3", item.Name);
        Assert.Null(item.Overview);
        _encoder.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("<movie><title>Unclosed")]
    [InlineData("<!DOCTYPE movie [<!ENTITY remote SYSTEM 'https://example.invalid/secret'>]><movie><title>&remote;</title></movie>")]
    public void MalformedOrExternalEntityMetadata_IsIgnoredWithoutHidingFile(string xml)
    {
        File.WriteAllText(_nfo, xml);
        var item = _items.Resolve(_id)!;
        _items.LoadLocalMetadata(item);
        Assert.Equal("Chapter.mp3", item.Name);
        _encoder.VerifyNoOtherCalls();
    }

    [Fact]
    public void DeniedUser_DoesNotResolveEvenWhenIdIsKnown()
    {
        var user = new User("limited", "local", "local");
        Assert.Null(_items.Resolve(_id, user));
        _encoder.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Probe_IsOnDemandCachedIsolatedAndDisposable()
    {
        var readPath = ReadAddress(_media);
        _encoder.Setup(encoder => encoder.GetMediaInfo(It.Is<MediaInfoRequest>(request => request.MediaSource.Path == readPath && request.MediaSource.Protocol == MediaProtocol.File), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MediaInfo
            {
                Container = "mp3",
                RunTimeTicks = TimeSpan.FromMinutes(10).Ticks,
                MediaStreams = [new MediaStream { Index = 0, Type = MediaStreamType.Audio, Codec = "mp3", Channels = 2 }]
            });
        var item = _items.Resolve(_id)!;
        var first = await _items.PreparePlayback(item, TestContext.Current.CancellationToken);
        first.SupportsTranscoding = false;
        first.MediaStreams[0].Codec = "client-local change";
        var second = await _items.PreparePlayback(_items.Resolve(_id)!, TestContext.Current.CancellationToken);
        Assert.True(second.SupportsTranscoding);
        Assert.Equal("mp3", second.MediaStreams[0].Codec);
        _encoder.Verify(encoder => encoder.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _items.ClearCache();
        Assert.Null(_items.Resolve(_id)!.RunTimeTicks);
        await _items.PreparePlayback(_items.Resolve(_id)!, TestContext.Current.CancellationToken);
        _encoder.Verify(encoder => encoder.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task NativeProbe_KeepsSelectedPathPinnedUntilItCompletes()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw Xunit.Sdk.SkipException.ForSkip("Windows filesystem sharing is required.");
        }

        var directory = Path.GetDirectoryName(_media)!;
        _encoder.Setup(encoder => encoder.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Assert.ThrowsAny<IOException>(() => Directory.Move(directory, directory + "-moved"));
                Assert.ThrowsAny<IOException>(() => File.Move(_media, _media + ".moved"));
                return new MediaInfo { Container = "mp3" };
            });
        await _items.PreparePlayback(_items.Resolve(_id)!, TestContext.Current.CancellationToken);
        Directory.Move(directory, directory + "-moved");
    }

    [Fact]
    public async Task RemovedFile_CannotUseAnAlreadySelectedObjectForPlayback()
    {
        var item = _items.Resolve(_id)!;
        File.Delete(_media);
        await Assert.ThrowsAsync<FileNotFoundException>(() => _items.PreparePlayback(item, TestContext.Current.CancellationToken));
        _encoder.VerifyNoOtherCalls();
    }

    [Fact]
    public void Artwork_IsSelectedOnlyAndObservesReplacementAndRemoval()
    {
        var poster = Path.Combine(Path.GetDirectoryName(_media)!, "folder.jpg");
        File.WriteAllText(poster, "image attributes only");
        var item = _items.Resolve(_id)!;
        Assert.Empty(item.ImageInfos);
        using (var locked = File.Open(poster, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            _items.LoadLocalMetadata(item);
            Assert.Equal(poster, Assert.Single(item.ImageInfos).Path);
        }

        var firstTag = item.LiveContext.ImageTags[ImageType.Primary];
        File.WriteAllText(poster, "different image attributes");
        _items.LoadLocalMetadata(item);
        Assert.NotEqual(firstTag, item.LiveContext.ImageTags[ImageType.Primary]);
        File.Delete(poster);
        _items.LoadLocalMetadata(item);
        Assert.Empty(item.ImageInfos);
        Assert.Empty(item.LiveContext.ImageTags);
        _encoder.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Subtitles_OnlySelectedVideosMatchingSiblingsAndUpdatedWithoutReprobe()
    {
        var directory = Path.GetDirectoryName(_media)!;
        var video = Path.Combine(directory, "Movie.mp4");
        var subtitle = Path.Combine(directory, "Movie.en.forced.srt");
        await File.WriteAllTextAsync(video, "mock video", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(subtitle, "mock subtitle", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "Different.srt"), "not selected", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "Movie.playlist.m3u"), "https://example.invalid/", TestContext.Current.CancellationToken);
        var nested = Directory.CreateDirectory(Path.Combine(directory, "Nested"));
        await File.WriteAllTextAsync(Path.Combine(nested.FullName, "Movie.en.srt"), "not selected", TestContext.Current.CancellationToken);
        var group = Assert.Single(_library.GetLibraries());
        var id = _library.Browse(group.Id, TestContext.Current.CancellationToken).Single(entry => entry.Name == "Movie.mp4").Id;
        var readPath = ReadAddress(video);
        _encoder.Setup(encoder => encoder.GetMediaInfo(It.Is<MediaInfoRequest>(request => request.MediaSource.Path == readPath), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaInfo { MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Index = 0 }] });
        var item = _items.Resolve(id)!;
        _items.LoadLocalMetadata(item);
        _encoder.VerifyNoOtherCalls();
        var source = await _items.PreparePlayback(item, TestContext.Current.CancellationToken);
        var selected = Assert.Single(source.MediaStreams, stream => stream.IsExternal);
        Assert.Equal(subtitle, selected.Path);
        Assert.Equal("en", selected.Language);
        Assert.True(selected.IsForced);
        File.Delete(subtitle);
        var updated = await _items.PreparePlayback(item, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(updated.MediaStreams, stream => stream.IsExternal);
        _encoder.Verify(encoder => encoder.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static string ReadAddress(string path)
    {
        using var lease = LivePathLease.Acquire(path);
        return lease.ReadPath;
    }

    [Fact]
    public async Task LegacyMetadataRefresh_IsNoOpAndItemIsReadOnly()
    {
        var item = _items.Resolve(_id)!;
        Assert.False(item.CanDelete());
        await item.RefreshMetadata(new MediaBrowser.Controller.Providers.MetadataRefreshOptions(Mock.Of<MediaBrowser.Controller.Providers.IDirectoryService>()), TestContext.Current.CancellationToken);
        Assert.Equal("Chapter.mp3", item.Name);
        _encoder.VerifyNoOtherCalls();
    }

    public void Dispose()
    {
        _items.Dispose();
        _fixture.Delete(true);
    }
}
