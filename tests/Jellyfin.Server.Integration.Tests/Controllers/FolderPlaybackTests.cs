using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Api.Models.LiveFolderDtos;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class FolderPlaybackTests
{
    [Fact]
    public async Task Continue_DismissesOneOrAllWithoutErasingBookmarksAndPlaybackStartRestoresIt()
    {
        using var fixture = new LiveFolderFixture();
        fixture.Write("A.m4b", [1]);
        fixture.Write("B.m4b", [2]);
        await fixture.Start();
        var group = await fixture.AddGroup();
        var items = (await fixture.Browse(group.Id, fixture.Media)).Items;
        var userDto = await AuthHelper.GetUserDtoAsync(fixture.Client);
        var state = fixture.Services.GetRequiredService<ILiveUserDataStore>();
        var otherUser = Guid.NewGuid();
        foreach (var item in items)
        {
            state.Save(userDto.Id, item.Id, new UserItemData { Key = item.Id.ToString("N"), PlaybackPositionTicks = 123_000_000, IsFavorite = true });
            state.Save(otherUser, item.Id, new UserItemData { Key = item.Id.ToString("N"), PlaybackPositionTicks = 456_000_000 });
        }

        using (fixture.LockFiles())
        {
            var lists = fixture.Reader.Enumerations.Count;
            using var single = await fixture.Client.DeleteAsync($"Jigglefin/Continue?itemId={items[0].Id}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, single.StatusCode);
            Assert.Equal(items[1].Id, Assert.Single((await fixture.Query("UserItems/Resume")).Items).Id);
            using var all = await fixture.Client.DeleteAsync("Jigglefin/Continue", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, all.StatusCode);
            Assert.Empty((await fixture.Query("UserItems/Resume")).Items);
            Assert.All(items, item =>
            {
                var saved = state.Get(userDto.Id, item.Id);
                Assert.True(saved.HideFromResume);
                Assert.True(saved.IsFavorite);
                Assert.Equal(123_000_000, saved.PlaybackPositionTicks);
                Assert.False(state.Get(otherUser, item.Id).HideFromResume);
                Assert.Equal(456_000_000, state.Get(otherUser, item.Id).PlaybackPositionTicks);
            });

            var user = fixture.Services.GetRequiredService<IUserManager>().GetUserById(userDto.Id)!;
            var selected = fixture.Services.GetRequiredService<ILiveItemService>().Resolve(items[0].Id, user)!;
            var manager = fixture.Services.GetRequiredService<IUserDataManager>();
            manager.SaveUserData(user, selected, state.Get(user.Id, selected.Id), UserDataSaveReason.UpdateUserRating, TestContext.Current.CancellationToken);
            Assert.True(state.Get(user.Id, selected.Id).HideFromResume);
            manager.SaveUserData(user, selected, state.Get(user.Id, selected.Id), UserDataSaveReason.PlaybackStart, TestContext.Current.CancellationToken);
            Assert.Equal(selected.Id, Assert.Single((await fixture.Query("UserItems/Resume")).Items).Id);
            Assert.Equal(123_000_000, state.Get(user.Id, selected.Id).PlaybackPositionTicks);
            Assert.Equal(lists, fixture.Reader.Enumerations.Count);
        }

        await fixture.AssertNoCatalogImport();
    }

    [Fact]
    public async Task Favorites_ArePersonalLiveShortcutsWithoutReadingMediaOrEnumeratingDirectories()
    {
        using var fixture = new LiveFolderFixture();
        fixture.Write("Folder/Track.mp3", [1, 2, 3]);
        fixture.Write("Folder/Track.nfo", Encoding.UTF8.GetBytes("<movie><title>Do not read me</title></movie>"));
        fixture.Write("Unvisited/Deep/Secret.mp3", [4]);
        await fixture.Start();
        var group = await fixture.AddGroup();
        var folder = await fixture.Navigate(group.Id, "Folder");
        var track = await fixture.Navigate(group.Id, "Folder", "Track.mp3");
        var dto = await AuthHelper.GetUserDtoAsync(fixture.Client);
        var state = fixture.Services.GetRequiredService<ILiveUserDataStore>();
        foreach (var item in new[] { group, folder, track })
        {
            using var favorite = await fixture.Client.PostAsync($"UserFavoriteItems/{item.Id}", null, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, favorite.StatusCode);
        }

        state.Save(Guid.NewGuid(), Guid.NewGuid(), new UserItemData { Key = "other", IsFavorite = true });
        using (fixture.LockFiles())
        {
            var lists = fixture.Reader.Enumerations.Count;
            foreach (var route in new[] { "Items?isFavorite=true&recursive=true", $"Users/{dto.Id}/Items?filters=IsFavorite" })
            {
                var favorites = (await fixture.Query(route)).Items;
                Assert.Equal(new[] { group.Id, folder.Id, track.Id }.Order(), favorites.Select(item => item.Id).Order());
                var file = Assert.Single(favorites, item => item.Id.Equals(track.Id));
                Assert.Equal(folder.Id, file.ParentId);
                Assert.Equal("Track.mp3", file.Name);
                Assert.Equal(3, file.FileSize);
                Assert.NotNull(file.DateModified);
                Assert.Null(file.Overview);
                LiveFolderFixture.AssertUnprobedSource(file);
            }

            Assert.Equal(lists, fixture.Reader.Enumerations.Count);
            var users = fixture.Services.GetRequiredService<IUserManager>();
            var user = users.GetUserById(dto.Id)!;
            user.SetPermission(PermissionKind.EnableAllFolders, false);
            user.SetPreference(PreferenceKind.EnabledFolders, Array.Empty<Guid>());
            await users.UpdateUserAsync(user);
            var stats = fixture.Reader.Stats.Count;
            Assert.Empty((await fixture.Query("Items?isFavorite=true")).Items);
            Assert.Equal(stats, fixture.Reader.Stats.Count);
            Assert.True(state.Get(dto.Id, track.Id).IsFavorite);
        }

        await fixture.AssertNoCatalogImport();
    }

    [Theory]
    [InlineData("m3u", "#EXTM3U\r\nB.mp3\r\nA.mp3\r\nB.mp3\r\nChild/C.mp4\r\nhttps://example.invalid/no.mp3\r\n../escape.mp3\r\nC:\\outside.mp3\r\n\\\\invalid\\share\\no.mp3\r\nMissing.mp3\r\nnotes.txt", 6)]
    [InlineData("m3u8", "\uFEFF#EXTM3U\nB.mp3\n./A.mp3\nB.mp3\nChild\\C.mp4", 0)]
    [InlineData("pls", "[playlist]\nFile4=Child/C.mp4\nFile2=A.mp3\nTitle2=Ignored title\nFile1=B.mp3\nFile3=B.mp3\nNumberOfEntries=4\nVersion=2", 0)]
    public async Task Playlists_PreserveOrderAndDuplicatesAndReadOnlyNamedLocalDirectories(string extension, string content, int ignored)
    {
        using var fixture = new LiveFolderFixture();
        var a = fixture.Write("A.mp3", [1]);
        var b = fixture.Write("B.mp3", [2]);
        var c = fixture.Write("Child/C.mp4", [3]);
        fixture.Write("Unvisited/Deep/Hidden.mp3", [4]);
        fixture.Write("notes.txt", [5]);
        var playlistPath = fixture.Write("Order." + extension, Encoding.UTF8.GetBytes(content));
        await fixture.Start();
        var group = await fixture.AddGroup();
        var playlist = await fixture.Navigate(group.Id, "Order." + extension);
        using var lockA = File.Open(a, FileMode.Open, FileAccess.Read, FileShare.None);
        using var lockB = File.Open(b, FileMode.Open, FileAccess.Read, FileShare.None);
        using var lockC = File.Open(c, FileMode.Open, FileAccess.Read, FileShare.None);
        using var noPlaylistWrites = File.Open(playlistPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var lists = fixture.Reader.Enumerations.Count;
        var result = await fixture.Client.GetFromJsonAsync<LocalPlaylistDto>($"Jigglefin/Playlists/{playlist.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(new[] { "B.mp3", "A.mp3", "B.mp3", "C.mp4" }, result.Items.Select(item => item.Name));
        Assert.Equal(result.Items[0].Id, result.Items[2].Id);
        Assert.Equal(ignored, result.IgnoredEntries);
        Assert.Equal(new[] { fixture.ReadPathFor(string.Empty), fixture.ReadPathFor("Child") }.Select(path => path.TrimEnd(Path.DirectorySeparatorChar)), fixture.Reader.Enumerations.Skip(lists).Select(path => path.TrimEnd(Path.DirectorySeparatorChar)));
        Assert.All(result.Items, item => Assert.Null(item.MediaSources));
        await fixture.AssertNoCatalogImport();

        var userDto = await AuthHelper.GetUserDtoAsync(fixture.Client);
        var users = fixture.Services.GetRequiredService<IUserManager>();
        var user = users.GetUserById(userDto.Id)!;
        user.SetPermission(PermissionKind.EnableAllFolders, false);
        user.SetPreference(PreferenceKind.EnabledFolders, Array.Empty<Guid>());
        await users.UpdateUserAsync(user);
        var stats = fixture.Reader.Stats.Count;
        lists = fixture.Reader.Enumerations.Count;
        using var denied = await fixture.Client.GetAsync($"Jigglefin/Playlists/{playlist.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(stats, fixture.Reader.Stats.Count);
        Assert.Equal(lists, fixture.Reader.Enumerations.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Playlists_RejectOversizedFilesOrEntryCountsBeforeResolvingReferences(bool largeBytes)
    {
        using var fixture = new LiveFolderFixture();
        fixture.Write("Order.m3u", Encoding.UTF8.GetBytes(largeBytes ? new string('x', (1024 * 1024) + 1) : string.Concat(Enumerable.Repeat("track.mp3\n", 2001))));
        await fixture.Start();
        var group = await fixture.AddGroup();
        var playlist = await fixture.Navigate(group.Id, "Order.m3u");
        var lists = fixture.Reader.Enumerations.Count;
        using var response = await fixture.Client.GetAsync($"Jigglefin/Playlists/{playlist.Id}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(lists, fixture.Reader.Enumerations.Count);
    }

    [Fact]
    public async Task Picker_ListsLogicalDrivesAndImmediateDirectoriesWithoutReadingFiles()
    {
        using var fixture = new LiveFolderFixture();
        fixture.Write("One/Deep/no.mp3", [1]);
        fixture.Write("Two/other.mp3", [2]);
        fixture.Write("file.mp3", [3]);
        await fixture.Start();
        using (fixture.LockFiles())
        {
            var drives = await fixture.Client.GetFromJsonAsync<FileSystemEntryInfo[]>("Environment/Drives", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal(Environment.GetLogicalDrives(), drives!.Select(drive => drive.Path));
            var directories = await fixture.Client.GetFromJsonAsync<FileSystemEntryInfo[]>($"Environment/DirectoryContents?includeDirectories=true&path={Uri.EscapeDataString(fixture.Media)}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(directories);
            Assert.Equal(new[] { "One", "Two" }, directories!.Select(directory => directory.Name));
            Assert.Equal(fixture.PathFor("One"), directories[0].Path);
        }

        using var anonymous = fixture.NewClient();
        using var denied = await anonymous.GetAsync("Environment/Drives", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Empty(fixture.Reader.Enumerations);
        await fixture.AssertNoCatalogImport();
    }
}
