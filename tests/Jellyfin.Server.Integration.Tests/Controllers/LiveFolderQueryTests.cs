using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class LiveFolderQueryTests
{
    [Fact]
    public async Task Resume_UsesSavedPositionsInsideTheRequestedFolderWithoutScanningOrReadingMetadata()
    {
        using var fixture = new LiveFolderFixture();
        foreach (var path in new[] { "Books/Novel/01.m4b", "Books/Novel/Chapters/02.m4b", "Books/Novel Extra/03.m4b", "Books/Other/04.m4b", "Films/Clip.mp4", "Books/Novel/Missing.m4b", "Books/Novel/Notes.pdf" })
        {
            fixture.Write(path, [1, 2, 3]);
        }

        fixture.Write("Books/Novel/01.nfo", System.Text.Encoding.UTF8.GetBytes("<book><title>Must not be read for resume</title></book>"));
        await fixture.Start();
        var books = await fixture.AddGroup("Books", paths: [fixture.PathFor("Books")]);
        var films = await fixture.AddGroup("Films", paths: [fixture.PathFor("Films")]);
        var folders = await fixture.Browse(books.Id, fixture.PathFor("Books"));
        var novel = Assert.Single(folders.Items, item => item.Name == "Novel");
        var novelItems = await fixture.Browse(novel.Id, novel.Path);
        var first = Assert.Single(novelItems.Items, item => item.Name == "01.m4b");
        var chapters = Assert.Single(novelItems.Items, item => item.Name == "Chapters");
        var second = Assert.Single((await fixture.Browse(chapters.Id, chapters.Path)).Items);
        var similarlyNamed = Assert.Single(folders.Items, item => item.Name == "Novel Extra");
        var third = Assert.Single((await fixture.Browse(similarlyNamed.Id, similarlyNamed.Path)).Items);
        var other = Assert.Single(folders.Items, item => item.Name == "Other");
        var fourth = Assert.Single((await fixture.Browse(other.Id, other.Path)).Items);
        var clip = Assert.Single((await fixture.Browse(films.Id, fixture.PathFor("Films"))).Items);
        var missing = Assert.Single(novelItems.Items, item => item.Name == "Missing.m4b");
        var notes = Assert.Single(novelItems.Items, item => item.Name == "Notes.pdf");
        var user = await AuthHelper.GetUserDtoAsync(fixture.Client);
        var state = fixture.Services.GetRequiredService<ILiveUserDataStore>();
        var playedAt = DateTime.UtcNow;
        var ordered = new[] { first, second, third, fourth, clip, missing, notes, novel };
        foreach (var (item, index) in ordered.Select((item, index) => (item, index)))
        {
            state.Save(user.Id, item.Id, new UserItemData
            {
                Key = item.Id.ToString("N"), PlaybackPositionTicks = 12_000_000,
                LastKnownRunTimeTicks = 100_000_000, LastPlayedDate = playedAt.AddMinutes(-index),
                Played = item.Id.Equals(fourth.Id)
            });
        }

        File.Delete(missing.Path);
        using (fixture.LockFiles())
        {
            var listsBefore = fixture.Reader.Enumerations.Count;
            Assert.Equal(new[] { first.Id, second.Id, third.Id, clip.Id }, (await fixture.Query("UserItems/Resume")).Items.Select(item => item.Id));
            foreach (var route in new[] { "UserItems/Resume", $"Users/{user.Id}/Items/Resume" })
            {
                var inNovel = await fixture.Query($"{route}?parentId={novel.Id}&fields=Overview,MediaSources,MediaStreams");
                Assert.Equal(new[] { first.Id, second.Id }, inNovel.Items.Select(item => item.Id));
                Assert.All(inNovel.Items, item =>
                {
                    Assert.Null(item.Overview);
                    LiveFolderFixture.AssertUnprobedSource(item);
                    Assert.Null(item.MediaStreams);
                    Assert.Equal(12_000_000, item.UserData.PlaybackPositionTicks);
                });
                Assert.Equal("01.m4b", inNovel.Items[0].Name);
                Assert.Equal(second.Id, Assert.Single((await fixture.Query($"{route}?parentId={chapters.Id}")).Items).Id);
                Assert.Equal(new[] { first.Id, second.Id, third.Id }, (await fixture.Query($"{route}?parentId={books.Id}")).Items.Select(item => item.Id));
                Assert.Empty((await fixture.Query($"{route}?parentId={novel.Id}&mediaTypes=Video")).Items);
                Assert.Empty((await fixture.Query($"{route}?parentId={novel.Id}&excludeItemTypes=AudioBook")).Items);
                Assert.Equal(second.Id, Assert.Single((await fixture.Query($"{route}?parentId={novel.Id}&includeItemTypes=AudioBook&searchTerm=02")).Items).Id);
                var page = await fixture.Query($"{route}?parentId={novel.Id}&startIndex=1&limit=1&enableUserData=false");
                Assert.Equal(2, page.TotalRecordCount);
                Assert.Equal(1, page.StartIndex);
                Assert.Equal(second.Id, Assert.Single(page.Items).Id);
                Assert.Null(page.Items[0].UserData);
                Assert.Equal(0, (await fixture.Query($"{route}?parentId={novel.Id}&enableTotalRecordCount=false")).TotalRecordCount);
                using var notFolder = await fixture.Client.GetAsync($"{route}?parentId={first.Id}", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NotFound, notFolder.StatusCode);
            }

            Assert.Equal(listsBefore, fixture.Reader.Enumerations.Count);
            Assert.Equal(12_000_000, state.Get(user.Id, missing.Id).PlaybackPositionTicks);
        }

        await fixture.AssertNoCatalogImport();
    }

    [Fact]
    public async Task ImmediateQueries_ApplySavedStateFiltersSortingAndPagingWithoutHydration()
    {
        using var fixture = new LiveFolderFixture();
        fixture.Write("A.m4b", [1]);
        fixture.Write("B.m4b", [2]);
        fixture.Write("C.mp4", [3]);
        fixture.Write("Subfolder/Not Listed.m4b", [4]);
        await fixture.Start();
        var group = await fixture.AddGroup();
        var items = (await fixture.Browse(group.Id, fixture.Media)).Items;
        var a = Assert.Single(items, item => item.Name == "A.m4b");
        var b = Assert.Single(items, item => item.Name == "B.m4b");
        var c = Assert.Single(items, item => item.Name == "C.mp4");
        var folder = Assert.Single(items, item => item.IsFolder == true);
        var user = await AuthHelper.GetUserDtoAsync(fixture.Client);
        var state = fixture.Services.GetRequiredService<ILiveUserDataStore>();
        state.Save(user.Id, a.Id, new UserItemData { Key = a.Id.ToString("N"), IsFavorite = true, Likes = true, PlaybackPositionTicks = 10, PlayCount = 1, LastPlayedDate = DateTime.UtcNow.AddDays(-1) });
        state.Save(user.Id, b.Id, new UserItemData { Key = b.Id.ToString("N"), Played = true, Likes = false, PlayCount = 3, LastPlayedDate = DateTime.UtcNow });
        state.Save(user.Id, group.Id, new UserItemData { Key = group.Id.ToString("N"), IsFavorite = true });
        using (fixture.LockFiles())
        {
            foreach (var route in new[] { "Items", $"Users/{user.Id}/Items" })
            {
                foreach (var (filter, expected) in new (string, BaseItemDto[])[]
                {
                    ("filters=IsFolder", [folder]), ("filters=IsNotFolder", [a, b, c]),
                    ("filters=IsFavorite", [a]), ("isFavorite=true", [a]), ("isFavorite=false", [folder, b, c]),
                    ("filters=IsPlayed", [b]), ("isPlayed=true", [b]), ("filters=IsUnplayed", [folder, a, c]),
                    ("filters=IsResumable", [a]), ("filters=Likes", [a]), ("filters=Dislikes", [b]),
                    ("filters=IsFavoriteOrLikes", [a]), ("filters=IsFavorite,IsPlayed", []),
                    ("mediaTypes=Video", [c]), ("includeItemTypes=AudioBook&excludeItemIds=" + a.Id, [b]),
                    ("searchTerm=B.m4b", [b]), ("filters=IsNotFolder&sortBy=PlayCount&sortOrder=Descending", [b, a, c]),
                    ("filters=IsNotFolder&sortBy=DatePlayed&sortOrder=Descending", [b, a, c]),
                    ("filters=IsNotFolder&sortBy=CommunityRating", [a, b, c])
                })
                {
                    var result = await fixture.Browse(group.Id, fixture.Media, "&recursive=true&" + filter);
                    // The legacy route must bind the same filter parameters too.
                    var legacy = await fixture.Query($"{route}?parentId={group.Id}&recursive=true&{filter}");
                    Assert.Equal(expected.Select(item => item.Id), result.Items.Select(item => item.Id));
                    Assert.Equal(result.Items.Select(item => item.Id), legacy.Items.Select(item => item.Id));
                    Assert.Equal(expected.Length, result.TotalRecordCount);
                }

                var page = await fixture.Query($"{route}?parentId={group.Id}&filters=IsNotFolder&startIndex=1&limit=1&enableUserData=false");
                Assert.Equal(3, page.TotalRecordCount);
                Assert.Equal(b.Id, Assert.Single(page.Items).Id);
                Assert.Null(page.Items[0].UserData);
                Assert.Equal(group.Id, Assert.Single((await fixture.Query($"{route}?isFavorite=true")).Items).Id);
            }
        }

        await fixture.AssertNoCatalogImport();
    }
}
