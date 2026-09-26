using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Emby.Server.Implementations.Library.Live;
using MediaBrowser.Controller.Library;
using Xunit;
using Xunit.Sdk;

namespace Jellyfin.Server.Implementations.Tests.Library;

public sealed class LiveDirectoryBrowserTests
{
    private static readonly string _rootPath = Path.Combine(Path.GetTempPath(), "jigglefin-live-fixture");

    [Fact]
    public void Mount_ReadsOnlyRootAttributes_NoDirectoryEnumeration()
    {
        var reader = new RecordingReader();
        var browser = new LiveDirectoryBrowser(reader);

        var root = browser.Mount("Media", _rootPath);

        Assert.Equal(_rootPath, root.FullPath);
        Assert.Equal(new[] { _rootPath }, reader.StatCalls);
        Assert.Empty(reader.EnumerationCalls);
    }

    [Fact]
    public void Browse_ReadsOnlyImmediateEntries_NoChildStatOrDescendantEnumeration()
    {
        var reader = new RecordingReader();
        reader.Children.Add(DirectoryEntry("Unvisited"));
        reader.Children.Add(FileEntry("Chapter 01.mp3"));
        reader.Children.Add(FileEntry("Chapter 01.nfo"));
        var browser = new LiveDirectoryBrowser(reader);
        var root = browser.Mount("Media", _rootPath);
        reader.StatCalls.Clear();

        var entries = browser.Browse(root, string.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(3, entries.Count);
        Assert.Equal(new[] { _rootPath }, reader.EnumerationCalls);
        Assert.Equal(new[] { _rootPath }, reader.StatCalls);
        Assert.All(entries, item => Assert.Equal(root.Id, item.ParentId));
        Assert.Contains(entries, item => item.Name == "Unvisited" && item.File.IsDirectory);
        Assert.Contains(entries, item => item.Name == "Chapter 01.mp3" && item.File.Length == 42);
        Assert.Contains(entries, item => item.Name == "Chapter 01.nfo");
    }

    [Fact]
    public void Browse_ReflectsAddsAndRemovesWithoutRefreshOrCachedMembership()
    {
        var reader = new RecordingReader();
        reader.Children.Add(FileEntry("Old.mp3"));
        var browser = new LiveDirectoryBrowser(reader);
        var root = browser.Mount("Media", _rootPath);
        Assert.Equal("Old.mp3", Assert.Single(browser.Browse(root, string.Empty, TestContext.Current.CancellationToken)).Name);

        reader.Children.Clear();
        reader.Children.Add(FileEntry("New.mp3"));

        Assert.Equal("New.mp3", Assert.Single(browser.Browse(root, string.Empty, TestContext.Current.CancellationToken)).Name);
        Assert.Equal(new[] { _rootPath, _rootPath }, reader.EnumerationCalls);
    }

    [Fact]
    public void Browse_EmptyFolderHasAnEmptyCurrentListing()
    {
        var browser = new LiveDirectoryBrowser(new RecordingReader());
        var root = browser.Mount("Empty", _rootPath);
        Assert.Empty(browser.Browse(root, string.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Identities_SurviveNewBrowserAndRootDisplayNameChanges()
    {
        var reader = new RecordingReader();
        reader.Children.Add(FileEntry("Chapter.mp3"));
        var firstBrowser = new LiveDirectoryBrowser(reader);
        var firstRoot = firstBrowser.Mount("Old name", _rootPath);
        var firstEntry = Assert.Single(firstBrowser.Browse(firstRoot, string.Empty, TestContext.Current.CancellationToken));
        var secondBrowser = new LiveDirectoryBrowser(reader);
        var secondRoot = secondBrowser.Mount("New name", _rootPath + Path.DirectorySeparatorChar);

        Assert.Equal(firstRoot.Id, secondRoot.Id);
        Assert.Equal(firstEntry.Id, Assert.Single(secondBrowser.Browse(secondRoot, string.Empty, TestContext.Current.CancellationToken)).Id);
        Assert.NotEqual(firstRoot.Id, firstEntry.Id);
    }

    [Fact]
    public void SameNamedFilesInDifferentRoots_HaveDifferentIdentities()
    {
        var reader = new RecordingReader();
        var otherRootPath = _rootPath + "-other";
        reader.Attributes.Add(otherRootPath, new LiveFileInfo(otherRootPath, true, false, null, DateTime.UnixEpoch));
        var firstFile = FileEntry("Chapter.mp3");
        var secondFile = firstFile with { FullPath = Path.Combine(otherRootPath, "Chapter.mp3") };
        reader.Attributes.Add(firstFile.FullPath, firstFile);
        reader.Attributes.Add(secondFile.FullPath, secondFile);
        var browser = new LiveDirectoryBrowser(reader);
        var firstRoot = browser.Mount("Books", _rootPath);
        var secondRoot = browser.Mount("Books", otherRootPath);

        Assert.NotEqual(firstRoot.Id, secondRoot.Id);
        Assert.NotEqual(browser.GetEntry(firstRoot, "Chapter.mp3").Id, browser.GetEntry(secondRoot, "Chapter.mp3").Id);
    }

    [Theory]
    [InlineData("../Outside.mp3")]
    [InlineData("Sub/../../Outside.mp3")]
    [InlineData("Sub/../Inside.mp3")]
    [InlineData("/Outside.mp3")]
    [InlineData("C:Outside.mp3")]
    [InlineData("Inside.mp3:secret")]
    [InlineData("https://example.invalid/media.mp3")]
    public void UnsafePaths_AreRejectedBeforeFilesystemAccess(string relativePath)
    {
        var reader = new RecordingReader();
        var browser = new LiveDirectoryBrowser(reader);
        var root = browser.Mount("Media", _rootPath);
        reader.StatCalls.Clear();

        Assert.Throws<ArgumentException>(() => browser.GetEntry(root, relativePath));
        Assert.Throws<ArgumentException>(() => browser.Browse(root, relativePath, TestContext.Current.CancellationToken));
        Assert.Empty(reader.StatCalls);
        Assert.Empty(reader.EnumerationCalls);
    }

    [Fact]
    public void Browse_LinkIsVisibleButCannotBeOpenedOrTraversed()
    {
        var reader = new RecordingReader();
        var link = DirectoryEntry("External") with { IsLink = true };
        reader.Children.Add(link);
        reader.Attributes.Add(link.FullPath, link);
        var browser = new LiveDirectoryBrowser(reader);
        var root = browser.Mount("Media", _rootPath);

        Assert.True(Assert.Single(browser.Browse(root, string.Empty, TestContext.Current.CancellationToken)).File.IsLink);
        reader.EnumerationCalls.Clear();
        Assert.Throws<UnauthorizedAccessException>(() => browser.Browse(root, "External", TestContext.Current.CancellationToken));
        Assert.Throws<UnauthorizedAccessException>(() => browser.GetEntry(root, Path.Combine("External", "Secret.mp3")));
        Assert.Empty(reader.EnumerationCalls);
        Assert.DoesNotContain(Path.Combine(link.FullPath, "Secret.mp3"), reader.StatCalls);
    }

    [Fact]
    public void RootReplacedWithLink_IsRejectedOnNextBrowse()
    {
        var reader = new RecordingReader();
        var browser = new LiveDirectoryBrowser(reader);
        var root = browser.Mount("Media", _rootPath);
        reader.Attributes[_rootPath] = reader.Attributes[_rootPath] with { IsLink = true };

        Assert.Throws<UnauthorizedAccessException>(() => browser.Browse(root, string.Empty, TestContext.Current.CancellationToken));
        Assert.Empty(reader.EnumerationCalls);
    }

    [Fact]
    public void GetEntry_ChecksOnlySelectedPathAndAncestors()
    {
        var reader = new RecordingReader();
        var directory = DirectoryEntry("Books");
        var file = FileEntry(Path.Combine("Books", "Chapter.mp3"));
        reader.Attributes.Add(directory.FullPath, directory);
        reader.Attributes.Add(file.FullPath, file);
        var browser = new LiveDirectoryBrowser(reader);
        var root = browser.Mount("Media", _rootPath);
        reader.StatCalls.Clear();

        var selected = browser.GetEntry(root, Path.Combine("Books", "Chapter.mp3"));

        Assert.Equal("Chapter.mp3", selected.Name);
        Assert.Equal(new[] { _rootPath, directory.FullPath, file.FullPath }, reader.StatCalls);
        Assert.Empty(reader.EnumerationCalls);
        Assert.Equal(browser.GetEntry(root, "Books").Id, selected.ParentId);
    }

    [Fact]
    public void ReaderReturningOutsideEntry_FailsClosed()
    {
        var reader = new RecordingReader();
        reader.Children.Add(FileEntry("Wrong.mp3") with { FullPath = Path.Combine(_rootPath + "-other", "Wrong.mp3") });
        var browser = new LiveDirectoryBrowser(reader);
        var root = browser.Mount("Media", _rootPath);
        Assert.Throws<IOException>(() => browser.Browse(root, string.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Browse_CancellationDoesNotEnumerate()
    {
        var reader = new RecordingReader();
        var browser = new LiveDirectoryBrowser(reader);
        var root = browser.Mount("Media", _rootPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => browser.Browse(root, string.Empty, cancellation.Token));
        Assert.Empty(reader.EnumerationCalls);
    }

    [Fact]
    public void PhysicalReader_ListsLockedMediaAndSidecarsWithoutOpeningTheirContents()
    {
        var directory = Directory.CreateTempSubdirectory("jigglefin-live-test-");
        try
        {
            var mediaPath = Path.Combine(directory.FullName, "Chapter.mp3");
            var sidecarPath = Path.Combine(directory.FullName, "Chapter.nfo");
            File.WriteAllText(mediaPath, "Not a real media file: must not be probed during listing.");
            File.WriteAllText(sidecarPath, "Not XML: must not be parsed during listing.");
            Directory.CreateDirectory(Path.Combine(directory.FullName, "Unvisited", "Deep"));
            var before = Directory.GetFiles(directory.FullName)
                .ToDictionary(path => path, path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));
            using (var mediaLock = File.Open(mediaPath, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var sidecarLock = File.Open(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var browser = new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader());
                var root = browser.Mount("Media", directory.FullName);
                var entries = browser.Browse(root, string.Empty, TestContext.Current.CancellationToken);
                Assert.Equal(3, entries.Count);
                Assert.Contains(entries, item => item.Name == "Chapter.mp3");
                Assert.Contains(entries, item => item.Name == "Chapter.nfo");
                Assert.Contains(entries, item => item.Name == "Unvisited");
                Assert.DoesNotContain(entries, item => item.Name == "Deep");
            }

            Assert.All(before, pair => Assert.Equal(pair.Value, (new FileInfo(pair.Key).Length, File.GetLastWriteTimeUtc(pair.Key))));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void PhysicalReader_ReflectsChangesWithoutDatabaseOrRefresh()
    {
        var directory = Directory.CreateTempSubdirectory("jigglefin-live-test-");
        try
        {
            var browser = new LiveDirectoryBrowser(new PhysicalLiveDirectoryReader());
            var root = browser.Mount("Media", directory.FullName);
            Assert.Empty(browser.Browse(root, string.Empty, TestContext.Current.CancellationToken));
            var file = Path.Combine(directory.FullName, "Added.mp3");
            File.WriteAllText(file, "test");
            var entry = Assert.Single(browser.Browse(root, string.Empty, TestContext.Current.CancellationToken));
            Assert.Equal("Added.mp3", entry.Name);
            File.Delete(file);
            Assert.Empty(browser.Browse(root, string.Empty, TestContext.Current.CancellationToken));
            Assert.Throws<FileNotFoundException>(() => browser.GetEntry(root, "Added.mp3"));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static LiveFileInfo DirectoryEntry(string relativePath)
        => new(Path.Combine(_rootPath, relativePath), true, false, null, DateTime.UnixEpoch);

    [Fact]
    public void PhysicalReader_RejectsRootBelowLinkedAncestor()
    {
        var fixture = Directory.CreateTempSubdirectory("jigglefin-link-root-");
        var alias = Path.Combine(fixture.FullName, "Alias");
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Target"));
            Directory.CreateDirectory(Path.Combine(target.FullName, "Sub"));
            try
            {
                Directory.CreateSymbolicLink(alias, target.FullName);
            }
            catch (UnauthorizedAccessException)
            {
                throw SkipException.ForSkip("Creating the test link requires local symlink privileges.");
            }

            var reader = new PhysicalLiveDirectoryReader();
            var browser = new LiveDirectoryBrowser(reader);
            Assert.Throws<UnauthorizedAccessException>(() => browser.Mount("Unsafe", Path.Combine(alias, "Sub")));
            Assert.Throws<UnauthorizedAccessException>(() => reader.EnumerateDirectory(alias, TestContext.Current.CancellationToken).ToArray());
        }
        finally
        {
            if (Directory.Exists(alias))
            {
                Directory.Delete(alias);
            }

            fixture.Delete(true);
        }
    }

    private static LiveFileInfo FileEntry(string relativePath)
        => new(Path.Combine(_rootPath, relativePath), false, false, 42, DateTime.UnixEpoch);

    private sealed class RecordingReader : ILiveDirectoryReader
    {
        public Dictionary<string, LiveFileInfo> Attributes { get; } = new()
        {
            [_rootPath] = new LiveFileInfo(_rootPath, true, false, null, DateTime.UnixEpoch)
        };

        public List<LiveFileInfo> Children { get; } = [];

        public List<string> StatCalls { get; } = [];

        public List<string> EnumerationCalls { get; } = [];

        public LiveFileInfo Stat(string path)
        {
            StatCalls.Add(path);
            return Attributes.TryGetValue(path, out var entry) ? entry : throw new FileNotFoundException("Unexpected filesystem access.", path);
        }

        public IEnumerable<LiveFileInfo> EnumerateDirectory(string path, CancellationToken cancellationToken)
        {
            EnumerationCalls.Add(path);
            Assert.Equal(_rootPath, path);
            return Children.ToArray();
        }
    }
}
