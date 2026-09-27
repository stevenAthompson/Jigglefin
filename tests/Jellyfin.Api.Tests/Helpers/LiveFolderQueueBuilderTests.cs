using System;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Api.Helpers;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers;

public sealed class LiveFolderQueueBuilderTests
{
    [Fact]
    public void Queue_CapsTracksAndNeverOpensFurtherDescendants()
    {
        var root = Guid.NewGuid();
        var library = new Mock<ILiveLibrary>(MockBehavior.Strict);
        var files = Enumerable.Range(1, 501).Select(index => Entry(root, $"Track {index}.mp3")).Append(Entry(root, "Unvisited", true)).ToArray();
        library.Setup(value => value.BrowseLimited(root, 10_000, It.IsAny<CancellationToken>())).Returns(files);
        var result = new LiveFolderQueueBuilder(library.Object, int.MaxValue, "asc", TestContext.Current.CancellationToken).Build(root);
        Assert.Equal(500, result.Items.Count);
        Assert.Equal(500, result.ItemLimit);
        Assert.True(result.LimitReached);
        Assert.Equal("Track 2.mp3", result.Items[1].Name);
        Assert.Equal("Track 500.mp3", result.Items[^1].Name);
        Assert.All(result.Items, item => Assert.Null(item.MediaSources));
        library.Verify(value => value.BrowseLimited(root, 10_000, It.IsAny<CancellationToken>()), Times.Once);
        library.VerifyNoOtherCalls();
    }

    [Fact]
    public void Queue_CapsRawEntriesEvenWhenNoneArePlayable()
    {
        var root = Guid.NewGuid();
        var library = new Mock<ILiveLibrary>(MockBehavior.Strict);
        var entries = Enumerable.Range(0, 9999).Select(index => Entry(root, index + ".txt")).Append(Entry(root, "Unvisited", true)).ToArray();
        library.Setup(value => value.BrowseLimited(root, 10_000, It.IsAny<CancellationToken>())).Returns(entries);
        var result = new LiveFolderQueueBuilder(library.Object, 500, "playlist", TestContext.Current.CancellationToken).Build(root);
        Assert.Empty(result.Items);
        Assert.True(result.LimitReached);
        Assert.Equal(10_000, result.EntriesExamined);
        Assert.Equal(1, result.FoldersRead);
    }

    [Theory]
    [InlineData(false, 100)]
    [InlineData(true, 17)]
    public void Queue_CapsWideAndDeepTrees(bool deep, int expectedReads)
    {
        var root = Guid.NewGuid();
        var library = new Mock<ILiveLibrary>(MockBehavior.Strict);
        library.Setup(value => value.BrowseLimited(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((Guid parent, int remaining, CancellationToken token) => deep
                ? [Entry(parent, "Next", true)]
                : parent.Equals(root) ? Enumerable.Range(0, 150).Select(index => Entry(parent, index.ToString(System.Globalization.CultureInfo.InvariantCulture), true)).ToArray() : []);
        var result = new LiveFolderQueueBuilder(library.Object, 500, "asc", TestContext.Current.CancellationToken).Build(root);
        Assert.Empty(result.Items);
        Assert.True(result.LimitReached);
        Assert.Equal(expectedReads, result.FoldersRead);
        library.Verify(value => value.BrowseLimited(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(expectedReads));
    }

    [Fact]
    public void Queue_SharesEntryBudgetAcrossFoldersAndSkipsLinksAndUnavailableMounts()
    {
        var root = Guid.NewGuid();
        var child = Entry(root, "Child", true);
        var link = Entry(root, "Link", true);
        link = link with { File = link.File with { IsLink = true } };
        var missing = Entry(root, "Missing", true) with { IsUnavailable = true };
        var library = new Mock<ILiveLibrary>(MockBehavior.Strict);
        library.Setup(value => value.BrowseLimited(root, 10_000, It.IsAny<CancellationToken>())).Returns([child, link, missing]);
        library.Setup(value => value.BrowseLimited(child.Id, 9997, It.IsAny<CancellationToken>())).Returns([Entry(child.Id, "Track.mp3")]);
        var result = new LiveFolderQueueBuilder(library.Object, 500, "playlist", TestContext.Current.CancellationToken).Build(root);
        Assert.Single(result.Items);
        Assert.False(result.LimitReached);
        Assert.Equal(4, result.EntriesExamined);
        Assert.Equal(2, result.FoldersRead);
    }

    [Fact]
    public void Queue_SkipsUnreadableFoldersAndHonorsCancellationBeforeAnyAccess()
    {
        var root = Guid.NewGuid();
        var library = new Mock<ILiveLibrary>(MockBehavior.Strict);
        library.Setup(value => value.BrowseLimited(root, 10_000, It.IsAny<CancellationToken>())).Throws<UnauthorizedAccessException>();
        var result = new LiveFolderQueueBuilder(library.Object, 1, "asc", TestContext.Current.CancellationToken).Build(root);
        Assert.Empty(result.Items);
        Assert.Equal(1, result.SkippedEntries);
        library.Invocations.Clear();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new LiveFolderQueueBuilder(library.Object, 500, "asc", cancelled.Token).Build(root));
        library.VerifyNoOtherCalls();
    }

    [Fact]
    public void Queue_PartialFailuresStillConsumeTheSharedRawEntryBudget()
    {
        var root = Guid.NewGuid();
        var first = Entry(root, "First", true);
        var last = Entry(root, "Last", true);
        var library = new Mock<ILiveLibrary>(MockBehavior.Strict);
        library.Setup(value => value.BrowseLimited(root, 10_000, It.IsAny<CancellationToken>())).Returns([first, last]);
        library.Setup(value => value.BrowseLimited(first.Id, 9998, It.IsAny<CancellationToken>())).Throws(new LiveDirectoryReadException(9997, new IOException()));
        library.Setup(value => value.BrowseLimited(last.Id, 1, It.IsAny<CancellationToken>())).Returns([Entry(last.Id, "Track.mp3")]);
        var result = new LiveFolderQueueBuilder(library.Object, 500, "asc", TestContext.Current.CancellationToken).Build(root);
        Assert.Single(result.Items);
        Assert.True(result.LimitReached);
        Assert.Equal(10_000, result.EntriesExamined);
        Assert.Equal(1, result.SkippedEntries);
    }

    [Fact]
    public void Queue_TypeSortUsesFileExtensionLikeTheFolderView()
    {
        var root = Guid.NewGuid();
        var library = new Mock<ILiveLibrary>(MockBehavior.Strict);
        library.Setup(value => value.BrowseLimited(root, 10_000, It.IsAny<CancellationToken>())).Returns([Entry(root, "A.mp3"), Entry(root, "B.m4b")]);
        var result = new LiveFolderQueueBuilder(library.Object, 500, "type", TestContext.Current.CancellationToken).Build(root);
        Assert.Equal(new[] { "B.m4b", "A.mp3" }, result.Items.Select(item => item.Name));
    }

    [Theory]
    [InlineData("asc", "A.mp3")]
    [InlineData("desc", "B.mp3")]
    [InlineData("newest", "B.mp3")]
    [InlineData("oldest", "A.mp3")]
    [InlineData("largest", "B.mp3")]
    [InlineData("smallest", "A.mp3")]
    public void Queue_UsesSelectedSort(string sort, string first)
    {
        var root = Guid.NewGuid();
        var a = Entry(root, "A.mp3");
        var b = Entry(root, "B.mp3");
        b = b with { File = b.File with { Length = 100, LastWriteTimeUtc = DateTime.UnixEpoch.AddDays(1) } };
        var library = new Mock<ILiveLibrary>(MockBehavior.Strict);
        library.Setup(value => value.BrowseLimited(root, 10_000, It.IsAny<CancellationToken>())).Returns([b, a]);
        var result = new LiveFolderQueueBuilder(library.Object, 1, sort, TestContext.Current.CancellationToken).Build(root);
        Assert.Equal(first, Assert.Single(result.Items).Name);
    }

    private static LiveDirectoryEntry Entry(Guid parent, string name, bool directory = false)
        => new(Guid.NewGuid(), parent, parent, name, name, new LiveFileInfo(Path.Combine(Path.GetTempPath(), name), directory, false, 1, DateTime.UnixEpoch));
}
