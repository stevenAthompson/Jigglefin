using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace Jellyfin.Server.Integration.Tests.Controllers;

// Historical filename retained so the old rescan coverage is replaced, not hidden.
// No request in these regressions initiates a scan or invokes LibraryManager.
public sealed class FolderFirstRescanTests
{
    [Theory]
    [InlineData("movies", "Named Item.mp4", BaseItemKind.Video)]
    [InlineData("tvshows", "Named Item - S01E01.mp4", BaseItemKind.Video)]
    [InlineData("books", "Named Item.pdf", BaseItemKind.Book)]
    [InlineData("music", "Track 01.mp3", BaseItemKind.Audio)]
    [InlineData("books", "Chapter.m4b", BaseItemKind.AudioBook)]
    [InlineData("homevideos", "Photo.png", BaseItemKind.Photo)]
    public async Task RemovingAndRestoringLastFile_IsImmediatelyVisibleAndNeverChangesFolderKind(
        string collectionType,
        string filename,
        BaseItemKind fileKind)
    {
        using var fixture = new LiveFolderFixture();
        var path = fixture.Write("Action/Named Item/" + filename, [1, 2, 3]);
        await fixture.Start();
        using (fixture.LockFiles())
        {
            var group = await fixture.AddGroup(collectionType: collectionType);
            var folder = await fixture.Navigate(group.Id, "Action", "Named Item");
            Assert.Equal(BaseItemKind.Folder, folder.Type);
            var original = Assert.Single((await fixture.Browse(folder.Id, fixture.PathFor("Action/Named Item"))).Items);
            Assert.Equal(fileKind, original.Type);
            Assert.Equal(filename, original.Name);
        }

        var root = Assert.Single((await fixture.Query("UserViews")).Items);
        var stableFolder = await fixture.Navigate(root.Id, "Action", "Named Item");
        var originalFile = Assert.Single((await fixture.Browse(stableFolder.Id, fixture.PathFor("Action/Named Item"))).Items);
        File.Delete(path); // test-owned media mutation, no server refresh
        var emptyFolder = await fixture.Navigate(root.Id, "Action", "Named Item");
        Assert.Equal(stableFolder.Id, emptyFolder.Id);
        Assert.Equal(BaseItemKind.Folder, emptyFolder.Type);
        Assert.Empty((await fixture.Browse(emptyFolder.Id, fixture.PathFor("Action/Named Item"))).Items);
        using var stale = await fixture.Client.GetAsync($"Items/{originalFile.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);

        fixture.Write("Action/Named Item/" + filename, [4, 5, 6]);
        using (fixture.LockFiles())
        {
            var restored = Assert.Single((await fixture.Browse(emptyFolder.Id, fixture.PathFor("Action/Named Item"))).Items);
            Assert.Equal(originalFile.Id, restored.Id);
            Assert.Equal(fileKind, restored.Type);
            Assert.Equal(path, restored.Path);
        }

        await fixture.AssertNoCatalogImport();
    }

    [Theory]
    [InlineData("movies", ".mp4", BaseItemKind.Video, "Videos", false)]
    [InlineData("tvshows", ".mp4", BaseItemKind.Video, "Videos", true)]
    [InlineData("music", ".m4a", BaseItemKind.Audio, "Audio", false)]
    [InlineData("books", ".m4b", BaseItemKind.AudioBook, "Audio", false)]
    public async Task RenameAndRemoval_UpdateNavigationAndStreamsWithoutRefresh(
        string collectionType,
        string extension,
        BaseItemKind kind,
        string route,
        bool season)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JIGGLEFIN_TEST_FFMPEG")))
        {
            throw SkipException.ForSkip("Set JIGGLEFIN_TEST_FFMPEG to verify real selected-file playback.");
        }

        using var fixture = new LiveFolderFixture(enableEncoder: true);
        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample" + (route == "Audio" ? ".m4b" : ".mp4")),
            TestContext.Current.CancellationToken);
        var relative = season ? "Action/Original/Season 1/Original" : "Action/Original/Original";
        fixture.Write(relative + extension, bytes);
        await fixture.Start();
        var group = await fixture.AddGroup(collectionType: collectionType);
        var category = await fixture.Navigate(group.Id, "Action");
        var originalFolder = await fixture.Navigate(group.Id, "Action", "Original");
        var original = await fixture.Navigate(group.Id, (relative + extension).Split('/'));
        Assert.Equal(kind, original.Type);
        var user = await AuthHelper.GetUserDtoAsync(fixture.Client);
        var state = fixture.Services.GetRequiredService<ILiveUserDataStore>();
        state.Save(user.Id, original.Id, new UserItemData { Key = original.Id.ToString("N"), PlaybackPositionTicks = 6500000, LastKnownRunTimeTicks = 20000000 });

        Directory.Move(fixture.PathFor("Action/Original"), fixture.PathFor("Action/Renamed"));
        var newRelative = season ? "Action/Renamed/Season 1/Renamed" : "Action/Renamed/Renamed";
        var movedName = season ? "Action/Renamed/Season 1/Original" : "Action/Renamed/Original";
        File.Move(fixture.PathFor(movedName + extension), fixture.PathFor(newRelative + extension));
        var renamedFolder = Assert.Single((await fixture.Browse(category.Id, fixture.PathFor("Action"))).Items);
        Assert.Equal("Renamed", renamedFolder.Name);
        Assert.Equal(BaseItemKind.Folder, renamedFolder.Type);
        Assert.NotEqual(originalFolder.Id, renamedFolder.Id);
        var renamed = await fixture.Navigate(group.Id, (newRelative + extension).Split('/'));
        Assert.Equal(kind, renamed.Type);
        Assert.NotEqual(original.Id, renamed.Id);
        Assert.Equal(0, renamed.UserData.PlaybackPositionTicks);
        Assert.Equal(6500000, state.Get(user.Id, original.Id).PlaybackPositionTicks);
        Assert.Empty((await fixture.Query("UserItems/Resume")).Items);
        using var stale = await fixture.Client.GetAsync($"{route}/{original.Id}/stream?static=true", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        using var stream = await fixture.Client.GetAsync($"{route}/{renamed.Id}/stream?static=true", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Equal(bytes, await stream.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        Directory.Delete(fixture.PathFor("Action/Renamed"), true);
        Assert.Empty((await fixture.Browse(category.Id, fixture.PathFor("Action"))).Items);
        using var removed = await fixture.Client.GetAsync($"Items/{renamed.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.Equal(6500000, state.Get(user.Id, original.Id).PlaybackPositionTicks);
        await fixture.AssertNoCatalogImport();
    }
}
