using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Emby.Server.Implementations.Library.Live;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class LiveLibraryStoreTests : IDisposable
{
    private readonly DirectoryInfo _fixture = Directory.CreateTempSubdirectory("jigglefin-live-store-test-");
    private readonly TrackingReader _reader = new();
    private readonly string _media;
    private readonly string _state;
    private readonly LiveLibraryStore _store;

    public LiveLibraryStoreTests()
    {
        _media = Directory.CreateDirectory(Path.Combine(_fixture.FullName, "Media")).FullName;
        _state = Path.Combine(_fixture.FullName, "State");
        _store = CreateStore();
    }

    [Fact]
    public void Migration_ImportsOfflineRootsAndOnlySavedAddressesWithoutAnyMediaAccess()
    {
        var offline = LiveDirectoryBrowser.DescribeRoot("Offline", Path.Combine(_fixture.FullName, "Disconnected"));
        var group = new LiveLibraryDefinition(Guid.NewGuid(), "Books", [offline]) { Enabled = false };
        _store.ImportConfiguration([group]);
        _store.ImportConfiguration([group]);
        var saved = _store.ImportSavedAddress(Path.Combine(offline.FullPath, "Book", "Chapter.mp3"));
        Assert.Equal(LiveDirectoryBrowser.EntryId(offline, Path.Combine("Book", "Chapter.mp3")), saved);
        Assert.Equal(group.Id, _store.FindLibrary(saved!.Value)!.Id);
        Assert.Equal(group.Id, _store.FindLibrary(LiveDirectoryBrowser.EntryId(offline, "Book"))!.Id);
        Assert.False(Assert.Single(_store.GetLibraries()).Enabled);
        Assert.False(LiveLibraryAccess.CanAccess(null, Assert.Single(_store.GetLibraries())));
        _store.SetEnabled(group.Id, true);
        Assert.True(LiveLibraryAccess.CanAccess(null, Assert.Single(_store.GetLibraries())));
        Assert.Equal(group.Id, _store.FindLibrary(saved.Value)!.Id);
        Assert.Null(_store.ImportSavedAddress(Path.Combine(_fixture.FullName, "Other", "Chapter.mp3")));
        Assert.Empty(_reader.StatCalls);
        Assert.Empty(_reader.EnumerationCalls);
        Assert.False(Directory.Exists(offline.FullPath));
    }

    [Fact]
    public void Migration_ConflictsDoNotOverwriteExistingSettingsOrPartiallyImportRoots()
    {
        var root = LiveDirectoryBrowser.DescribeRoot("Media", _media);
        var first = new LiveLibraryDefinition(Guid.NewGuid(), "First", [root]);
        var overlapping = new LiveLibraryDefinition(Guid.NewGuid(), "Second", [root]);
        Assert.Throws<ArgumentException>(() => _store.ImportConfiguration([first, overlapping]));
        Assert.Empty(_store.GetLibraries());
        _store.ImportConfiguration([first]);
        Assert.Throws<InvalidDataException>(() => _store.ImportConfiguration([first with { Name = "Changed" }]));
        Assert.Equal("First", Assert.Single(_store.GetLibraries()).Name);
        var unsafeRoot = LiveDirectoryBrowser.DescribeRoot("Private", _state);
        Assert.Throws<ArgumentException>(() => _store.ImportConfiguration([new LiveLibraryDefinition(Guid.NewGuid(), "Unsafe", [unsafeRoot])]));
        Assert.Empty(_reader.StatCalls);
        Assert.Empty(_reader.EnumerationCalls);
    }

    [Fact]
    public void DisabledGroup_IsCommittedDisabledAndStaysDisabledAfterRestart()
    {
        var group = _store.AddLibrary("Unavailable", [_media], enabled: false);
        Assert.False(group.Enabled);
        Assert.False(Assert.Single(_store.GetLibraries()).Enabled);
        Assert.False(Assert.Single(CreateStore().GetLibraries()).Enabled);
        Assert.False(LiveLibraryAccess.CanAccess(null, group));
        Assert.Empty(_reader.EnumerationCalls);
    }

    [Fact]
    public void StartupAndConfigurationQueries_DoNotAccessMedia()
    {
        Assert.Empty(_store.GetLibraries());
        var library = _store.AddLibrary("Books", [_media]);
        Assert.Equal(new[] { _media }, _reader.StatCalls);
        Assert.Empty(_reader.EnumerationCalls);
        _reader.StatCalls.Clear();

        var restarted = CreateStore();
        Assert.Equal(library.Id, Assert.Single(restarted.GetLibraries()).Id);
        Assert.Equal(library.Id, restarted.FindLibrary(library.Id)!.Id);
        Assert.Empty(_reader.StatCalls);
        Assert.Empty(_reader.EnumerationCalls);
    }

    [Fact]
    public void MediaCannotOverlapWritableCacheLogOrTranscodePaths()
    {
        var store = new LiveLibraryStore(new LiveDirectoryBrowser(_reader), _state, [Path.Combine(_media, "Transcodes")]);
        Assert.Throws<ArgumentException>(() => store.AddLibrary("Unsafe", [_media]));
        Assert.Empty(store.GetLibraries());
        Assert.Empty(_reader.EnumerationCalls);
        Assert.False(Directory.Exists(Path.Combine(_media, "Transcodes")));
    }

    [Fact]
    public void MountAndBrowse_NeverDiscoverDescendantsUntilNavigation()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_media, "Unvisited"));
        File.WriteAllText(Path.Combine(nested.FullName, "Chapter.mp3"), "fixture");
        var library = _store.AddLibrary("Media", [_media]);
        Assert.Empty(_reader.EnumerationCalls);
        var folder = Assert.Single(_store.Browse(library.Id, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { _media }, _reader.EnumerationCalls);
        Assert.DoesNotContain(nested.FullName, _reader.StatCalls);

        var child = Assert.Single(_store.Browse(folder.Id, TestContext.Current.CancellationToken));
        Assert.Equal("Chapter.mp3", child.Name);
        Assert.Equal(new[] { _media, nested.FullName }, _reader.EnumerationCalls);
        Assert.DoesNotContain(child.File.FullPath, _reader.StatCalls);
    }

    [Fact]
    public void RememberedAddresses_AreNotDirectoryMembership()
    {
        var file = Path.Combine(_media, "Old.mp3");
        File.WriteAllText(file, "fixture");
        var library = _store.AddLibrary("Books", [_media]);
        var old = Assert.Single(_store.Browse(library.Id, TestContext.Current.CancellationToken));
        File.Delete(file);
        File.WriteAllText(Path.Combine(_media, "New.mp3"), "fixture");

        var restarted = CreateStore();
        Assert.Equal("New.mp3", Assert.Single(restarted.Browse(library.Id, TestContext.Current.CancellationToken)).Name);
        Assert.Throws<FileNotFoundException>(() => restarted.GetEntry(old.Id));
    }

    [Fact]
    public void CachedClientId_ResolvesAfterRestartAndRenameWithoutScanning()
    {
        File.WriteAllText(Path.Combine(_media, "Chapter.mp3"), "fixture");
        var library = _store.AddLibrary("Books", [_media]);
        var item = Assert.Single(_store.Browse(library.Id, TestContext.Current.CancellationToken));
        _store.RenameLibrary("Books", "Audiobooks");
        _reader.EnumerationCalls.Clear();
        _reader.StatCalls.Clear();

        var restarted = CreateStore();
        Assert.Equal("Audiobooks", restarted.FindLibrary(item.Id)!.Name);
        Assert.Empty(_reader.StatCalls);
        Assert.Equal(item.Id, restarted.GetEntry(item.Id)!.Id);
        Assert.Empty(_reader.EnumerationCalls);
    }

    [Fact]
    public void RemovedRoots_CannotResolveRememberedAddresses()
    {
        File.WriteAllText(Path.Combine(_media, "Chapter.mp3"), "fixture");
        var library = _store.AddLibrary("Books", [_media]);
        var item = Assert.Single(_store.Browse(library.Id, TestContext.Current.CancellationToken));
        _reader.StatCalls.Clear();
        _reader.EnumerationCalls.Clear();

        _store.RemoveLibrary("Books");
        Assert.Null(_store.FindLibrary(item.Id));
        Assert.Null(_store.GetEntry(item.Id));
        Assert.Empty(_reader.StatCalls);
        Assert.Empty(_reader.EnumerationCalls);
        Assert.True(File.Exists(item.File.FullPath));
    }

    [Fact]
    public void MultipleRoots_ListConfiguredLocationsWithoutEnumeratingThem()
    {
        var other = Directory.CreateDirectory(Path.Combine(_fixture.FullName, "Other"));
        var library = _store.AddLibrary("Media", [_media, other.FullName]);
        var entries = _store.Browse(library.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, item => Assert.True(item.File.IsDirectory));
        Assert.Empty(_reader.EnumerationCalls);
    }

    [Fact]
    public void StateAndMediaPaths_CannotOverlap()
    {
        Assert.Throws<ArgumentException>(() => _store.AddLibrary("Invalid", [_fixture.FullName]));
        Assert.Throws<ArgumentException>(() => _store.AddLibrary("Invalid", [_state]));
        Assert.Empty(_store.GetLibraries());
        Assert.Empty(_reader.EnumerationCalls);
    }

    [Fact]
    public void OverlappingRoots_AreRejectedWithoutEnumeration()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_media, "Child"));
        _store.AddLibrary("Media", [_media]);
        Assert.Throws<ArgumentException>(() => _store.AddLibrary("Duplicate", [_media]));
        Assert.Throws<ArgumentException>(() => _store.AddLibrary("Nested", [nested.FullName]));
        Assert.Single(_store.GetLibraries());
        Assert.Empty(_reader.EnumerationCalls);
    }

    [Fact]
    public void OverlappingPathsInSingleRequest_AreRejectedAtomically()
    {
        var nested = Directory.CreateDirectory(Path.Combine(_media, "Child"));
        Assert.Throws<ArgumentException>(() => _store.AddLibrary("Invalid", [_media, nested.FullName]));
        Assert.Empty(_store.GetLibraries());
        Assert.Empty(_reader.EnumerationCalls);
    }

    public void Dispose() => _fixture.Delete(true);

    private LiveLibraryStore CreateStore() => new(new LiveDirectoryBrowser(_reader), _state);

    private sealed class TrackingReader : ILiveDirectoryReader
    {
        private readonly PhysicalLiveDirectoryReader _physical = new();

        public List<string> StatCalls { get; } = [];

        public List<string> EnumerationCalls { get; } = [];

        public LiveFileInfo Stat(string path)
        {
            StatCalls.Add(path);
            return _physical.Stat(path);
        }

        public IEnumerable<LiveFileInfo> EnumerateDirectory(string path, CancellationToken cancellationToken)
        {
            EnumerationCalls.Add(path);
            return _physical.EnumerateDirectory(path, cancellationToken);
        }
    }
}
