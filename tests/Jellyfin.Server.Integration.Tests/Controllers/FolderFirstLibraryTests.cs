using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.UserDtos;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Security;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace Jellyfin.Server.Integration.Tests.Controllers;

// Replaces scan/type-collapse expectations with the same physical layout cases.
// The test's walk represents explicit successive user navigation, never a server scan.
public sealed class FolderFirstLibraryTests
{
    private static readonly string[] _files =
    [
        "Loose Movie.mp4", "Loose Track.m4a", "Loose Book.pdf", "Loose Chapter.m4b", "Loose Photo.png",
        "Action/Shared Movie.mp4", "Action/Shared Movie/Shared Movie.mp4",
        "Action/Shared Movie/extras/Trailer.mp4", "Action/Shared Movie/Bonus/Extra.mp4",
        "Action/Double Feature/First Feature.mp4", "Action/Double Feature/Second Feature.mp4",
        "Action/First Feature/First Feature.mp4", "Action/First Feature/Second Feature.mp4",
        "Drama/Same Show/Season 1/Same Show - S01E01.mp4",
        "Drama/Same Show/Season 1/Season Bonus/Season Bonus Clip.mp4",
        "Drama/Same Show/Bonus Collection/Unnumbered Clip.mp4",
        "Music/Same Artist/Same Album/Track 01.m4a", "Music/Same Artist/Same Album/Track 02.m4a",
        "Music/Same Artist/Same Album/Bonus Video.mp4",
        "Music/Same Artist/Same Album/Other recordings/Live.m4a",
        "Reading/Shared Book.pdf", "Reading/Shared Book/Shared Book.pdf",
        "Reading/Shared Book/Related/Other Book.epub",
        "Listening/Shared Audio.m4b", "Listening/Shared Audio/Shared Audio.m4b",
        "Listening/Shared Audio/Chapters/Chapter 1.m4b", "Listening/Shared Audio/Chapters/Chapter 2.m4b",
        "Listening/Shared Audio/Chapters/Extra.m4b", "Listening/Shared Audio/Booklet/Notes.pdf",
        "Holidays/Mixed Day/Clip.mp4", "Holidays/Mixed Day/Snapshot.png",
        "Holidays/Photos Only/Snapshot.png", "Holidays/Nested Day/Snapshot.png",
        "Holidays/Nested Day/More Photos/Another.png",
        "Discs/Disc Feature/VIDEO_TS/VIDEO_TS.IFO", "Discs/Disc Feature/VIDEO_TS/VTS_01_1.VOB",
        "Discs/Disc Feature/Bonus Film/Bonus Film.mp4", "Discs/Disc Feature/Snapshot.png",
        "Discs/Disc Feature/Disc Feature.mp4",
        "Discs/Disc Collection/Disc 1/VIDEO_TS/VIDEO_TS.IFO", "Discs/Disc Collection/Disc 1/VIDEO_TS/VTS_01_1.VOB",
        "Discs/Disc Collection/Other Film/Other Film.mp4",
        "Solo Category/Only DVD/VIDEO_TS/VIDEO_TS.IFO", "Solo Category/Only DVD/VIDEO_TS/VTS_01_1.VOB",
        "Single Disc Category/Disc 1/VIDEO_TS/VIDEO_TS.IFO", "Single Disc Category/Disc 1/VIDEO_TS/VTS_01_1.VOB",
        "Metadata/movie.nfo", "Metadata/movie.xml", "Metadata/metadata.opf", "Metadata/movie.strm",
        "Unicode/Les Misérables [édition 1]/Chapitre 01.M4B", "Unknown/Keep Me.weird"
    ];

    [Theory]
    [InlineData("movies")]
    [InlineData("tvshows")]
    [InlineData("music")]
    [InlineData("books")]
    [InlineData("homevideos")]
    [InlineData("musicvideos")]
    public async Task EveryConfiguredType_ListsExactlyTheCurrentMixedPhysicalTree(string collectionType)
    {
        using var fixture = new LiveFolderFixture();
        foreach (var file in _files)
        {
            fixture.Write(file, [1, 2, 3]); // invalid content must never be opened/probed while browsing
        }

        foreach (var empty in new[] { "Action/Empty", "Drama/Same Show/Season 2", "Drama/Same Show/Empty Bonus", "Reading/Empty Shelf", "Nothing Here" })
        {
            fixture.MakeDirectory(empty);
        }

        var before = Snapshot(fixture.Media);
        var expected = Directory.GetFileSystemEntries(fixture.Media, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        await fixture.Start();
        using (fixture.LockFiles())
        {
            var group = await fixture.AddGroup(collectionType: collectionType);
            Assert.Equal(BaseItemKind.Folder, group.Type);
            Assert.True(group.IsFolder);
            Assert.Null(group.CollectionType);
            var user = await AuthHelper.GetUserDtoAsync(fixture.Client);
            Assert.Equal(group.Id, Assert.Single((await fixture.Query($"Users/{user.Id}/Views")).Items).Id);
            Assert.Equal(group.Id, Assert.Single((await fixture.Query($"UserViews?presetViews={collectionType}")).Items).Id);

            var queue = new Queue<(Guid Id, string Path)>();
            queue.Enqueue((group.Id, fixture.Media));
            var found = new List<string>();
            var identities = new HashSet<Guid>();
            while (queue.TryDequeue(out var directory))
            {
                var children = await fixture.Browse(directory.Id, directory.Path, "&recursive=true&fields=ChildCount,MediaSources,MediaStreams,Overview,Path");
                var expectedChildren = Directory.GetFileSystemEntries(directory.Path).Order(StringComparer.Ordinal).ToArray();
                Assert.Equal(expectedChildren, children.Items.Select(item => item.Path).Order(StringComparer.Ordinal));
                Assert.Equal(expectedChildren.Length, children.TotalRecordCount);
                Assert.Equal(children.Items.OrderByDescending(item => item.IsFolder == true).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).Select(item => item.Id), children.Items.Select(item => item.Id));
                foreach (var child in children.Items)
                {
                    Assert.True(identities.Add(child.Id), "Different physical paths shared an identity.");
                    Assert.Equal(directory.Id, child.ParentId);
                    Assert.Equal(Path.GetFileName(child.Path), child.Name);
                    Assert.Null(child.Overview);
                    Assert.Null(child.ChildCount);
                    LiveFolderFixture.AssertUnprobedSource(child);
                    Assert.True(child.MediaStreams is null or { Length: 0 });
                    Assert.Empty(child.ImageTags);
                    Assert.False(child.CanDelete);
                    found.Add(child.Path);
                    if (Directory.Exists(child.Path))
                    {
                        Assert.Equal(BaseItemKind.Folder, child.Type);
                        Assert.True(child.IsFolder);
                        queue.Enqueue((child.Id, child.Path));
                    }
                    else
                    {
                        Assert.Equal(ExpectedKind(child.Path), child.Type);
                        Assert.False(child.IsFolder);
                    }
                }

                var legacy = await fixture.Query($"Users/{user.Id}/Items?parentId={directory.Id}&recursive=true");
                Assert.Equal(children.Items.Select(item => item.Id), legacy.Items.Select(item => item.Id));
            }

            Assert.Equal(expected, found.Order(StringComparer.Ordinal));
            var firstLevel = await fixture.Browse(group.Id, fixture.Media);
            var paged = new List<Guid>();
            for (var index = 0; index < firstLevel.Items.Count; index++)
            {
                var page = await fixture.Browse(group.Id, fixture.Media, $"&startIndex={index}&limit=1&sortBy=IsFolder,SortName&includeItemTypes=Folder,Video,Audio,AudioBook,Book,Photo");
                Assert.Equal(firstLevel.TotalRecordCount, page.TotalRecordCount);
                paged.Add(Assert.Single(page.Items).Id);
            }

            Assert.Equal(firstLevel.Items.Select(item => item.Id), paged);
            // Unsupported catalog routes cannot manufacture a season or aggregate media.
            Assert.Empty((await fixture.Query($"Shows/{group.Id}/Seasons")).Items);
            Assert.Empty((await fixture.Query($"Items?parentId={group.Id}&includeItemTypes=Movie,Series,MusicAlbum&recursive=true")).Items);
        }

        Assert.Equal(before, Snapshot(fixture.Media));
        await fixture.AssertNoCatalogImport();
    }

    [Theory]
    [InlineData("movies", ".mp4", BaseItemKind.Video, "Videos")]
    [InlineData("tvshows", ".mp4", BaseItemKind.Video, "Videos")]
    [InlineData("music", ".m4a", BaseItemKind.Audio, "Audio")]
    [InlineData("books", ".m4b", BaseItemKind.AudioBook, "Audio")]
    public async Task MultipleRoots_KeepSameNamedPhysicalFilesSeparateAndStreamable(string collectionType, string extension, BaseItemKind kind, string route)
    {
        RequireEncoder();
        using var fixture = new LiveFolderFixture(enableEncoder: true);
        var bytes = await Sample(route);
        foreach (var root in new[] { "First Root", "Second Root" })
        {
            fixture.Write(root + "/Action/Same Title/Same File" + extension, bytes);
            fixture.Write(root + "/Action/Same Title/Extra File" + extension, bytes);
        }

        var before = Snapshot(fixture.Media);
        await fixture.Start();
        var group = await fixture.AddGroup("Two roots", collectionType, fixture.PathFor("First Root"), fixture.PathFor("Second Root"));
        var listsBefore = fixture.Reader.Enumerations.Count;
        var roots = await fixture.Query($"Items?parentId={group.Id}");
        Assert.Equal(listsBefore, fixture.Reader.Enumerations.Count);
        Assert.Equal(new[] { "First Root", "Second Root" }, roots.Items.Select(item => item.Name));
        var fileIds = new HashSet<Guid>();
        foreach (var root in roots.Items)
        {
            Assert.Equal(group.Id, root.ParentId);
            var category = Assert.Single((await fixture.Browse(root.Id, root.Path)).Items);
            var title = Assert.Single((await fixture.Browse(category.Id, category.Path)).Items);
            Assert.Equal(BaseItemKind.Folder, title.Type);
            var files = (await fixture.Browse(title.Id, title.Path)).Items;
            Assert.Equal(2, files.Count);
            foreach (var file in files)
            {
                Assert.Equal(kind, file.Type);
                Assert.True(fileIds.Add(file.Id));
                using var stream = await fixture.Client.GetAsync($"{route}/{file.Id}/stream?static=true", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
                Assert.Equal(bytes, await stream.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            }
        }

        Assert.Equal(4, fileIds.Count);
        Assert.Equal(before, Snapshot(fixture.Media));
        await fixture.AssertNoCatalogImport();
    }

    [Fact]
    public async Task DisconnectedMount_DoesNotHideAvailableLocationsAndRecoversWithoutRefresh()
    {
        using var fixture = new LiveFolderFixture();
        fixture.Write("Available/Keep.txt", [1]);
        var missing = fixture.MakeDirectory("Disconnected");
        await fixture.Start();
        var group = await fixture.AddGroup("Locations", paths: [fixture.PathFor("Available"), missing]);
        var original = await fixture.Query($"Items?parentId={group.Id}");
        var offlineId = Assert.Single(original.Items, item => item.Name == "Disconnected").Id;
        Directory.Delete(missing);

        var before = fixture.Reader.Enumerations.Count;
        var disconnected = await fixture.Query($"Items?parentId={group.Id}");
        Assert.Equal(before, fixture.Reader.Enumerations.Count);
        Assert.Equal(original.Items.Select(item => item.Id), disconnected.Items.Select(item => item.Id));
        var offline = Assert.Single(disconnected.Items, item => item.Id.Equals(offlineId));
        Assert.Equal(LocationType.Offline, offline.LocationType);
        Assert.True(offline.IsFolder);
        Assert.Contains("unavailable", offline.Overview, StringComparison.OrdinalIgnoreCase);
        using var unavailable = await fixture.Client.GetAsync($"Items?parentId={offlineId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        var available = Assert.Single(disconnected.Items, item => !item.Id.Equals(offlineId));
        Assert.Equal("Keep.txt", Assert.Single((await fixture.Browse(available.Id, available.Path)).Items).Name);

        fixture.Write("Disconnected/New.txt", [2]);
        var reconnected = Assert.Single((await fixture.Query($"Items?parentId={group.Id}")).Items, item => item.Id.Equals(offlineId));
        Assert.Equal(LocationType.FileSystem, reconnected.LocationType);
        Assert.Null(reconnected.Overview);
        Assert.Equal("New.txt", Assert.Single((await fixture.Browse(offlineId, missing)).Items).Name);
        await fixture.AssertNoCatalogImport();
    }

    [Fact]
    public async Task AccessRules_ProtectFoldersIdsSearchDetailsAssetsAndPlaybackBeforeMediaAccess()
    {
        RequireEncoder();
        using var fixture = new LiveFolderFixture(enableEncoder: true);
        var bytes = await Sample("Videos");
        fixture.Write("Allowed/Action/Allowed Film.mp4", bytes);
        fixture.Write("Blocked/Action/Blocked Film.mp4", bytes);
        await fixture.Start();
        var allowed = await fixture.AddGroup("Allowed", paths: [fixture.PathFor("Allowed")]);
        var blocked = await fixture.AddGroup("Blocked", paths: [fixture.PathFor("Blocked")]);
        var blockedFolder = Assert.Single((await fixture.Browse(blocked.Id, fixture.PathFor("Blocked"))).Items);
        var blockedFile = Assert.Single((await fixture.Browse(blockedFolder.Id, blockedFolder.Path)).Items);
        var allowedFolder = Assert.Single((await fixture.Browse(allowed.Id, fixture.PathFor("Allowed"))).Items);
        var allowedFile = Assert.Single((await fixture.Browse(allowedFolder.Id, allowedFolder.Path)).Items);
        var users = fixture.Services.GetRequiredService<IUserManager>();
        var limited = await users.CreateUserAsync("Limited");
        limited.SetPermission(PermissionKind.EnableAllFolders, false);
        limited.SetPreference(PreferenceKind.EnabledFolders, [allowed.Id]);
        await users.UpdateUserAsync(limited);
        await users.ChangePassword(limited.Id, "Temporary-test-only-Password1!");
        using var client = fixture.NewClient();
        using var login = new HttpRequestMessage(HttpMethod.Post, "Users/AuthenticateByName");
        login.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, AuthHelper.DummyAuthHeader);
        login.Content = JsonContent.Create(new AuthenticateUserByName { Username = limited.Username, Pw = "Temporary-test-only-Password1!" }, options: JsonDefaults.Options);
        using var loggedIn = await client.SendAsync(login, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, loggedIn.StatusCode);
        using var loginJson = JsonDocument.Parse(await loggedIn.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        client.DefaultRequestHeaders.AddAuthHeader(loginJson.RootElement.GetProperty("AccessToken").GetString()!);
        Assert.Equal(allowed.Id, Assert.Single((await fixture.Query("UserViews", client)).Items).Id);
        Assert.Equal(allowedFile.Id, Assert.Single((await fixture.Query($"Items?ids={allowedFile.Id}", client)).Items).Id);
        using var permittedStream = await client.GetAsync($"Videos/{allowedFile.Id}/stream?static=true", TestContext.Current.CancellationToken);
        Assert.Equal(bytes, await permittedStream.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        var beforeStats = fixture.Reader.Stats.Count;
        var beforeLists = fixture.Reader.Enumerations.Count;
        foreach (var url in new[]
        {
            $"Items?parentId={blocked.Id}", $"Items?parentId={blockedFolder.Id}", $"Items?ids={blockedFile.Id}",
            $"Items/{blockedFile.Id}", $"Items/{blockedFile.Id}/Images/Primary",
            $"Items/{blockedFile.Id}/PlaybackInfo", $"Videos/{blockedFile.Id}/stream?static=true"
        })
        {
            using var denied = await client.GetAsync(url, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }

        using var posted = await client.PostAsJsonAsync($"Items/{blockedFile.Id}/PlaybackInfo", new { }, JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, posted.StatusCode);
        Assert.Empty((await fixture.Query("Items?searchTerm=Blocked&recursive=true", client)).Items);
        Assert.Equal(beforeStats, fixture.Reader.Stats.Count);
        Assert.Equal(beforeLists, fixture.Reader.Enumerations.Count);
        using var anonymous = fixture.NewClient();
        using var anonymousStream = await anonymous.GetAsync($"Videos/{blockedFile.Id}/stream?static=true", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, anonymousStream.StatusCode);
        Assert.Equal(beforeStats, fixture.Reader.Stats.Count);
        Assert.Equal(beforeLists, fixture.Reader.Enumerations.Count);

        var keys = fixture.Services.GetRequiredService<IAuthenticationManager>();
        await keys.CreateApiKey("Test administrator");
        using var api = fixture.NewClient();
        api.DefaultRequestHeaders.AddAuthHeader(Assert.Single(await keys.GetApiKeys()).AccessToken);
        using var apiStream = await api.GetAsync($"Videos/{blockedFile.Id}/stream?static=true", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, apiStream.StatusCode);
        Assert.Equal(bytes, await apiStream.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        // Legacy blocked-folder preferences must remain restrictive too.
        limited.SetPermission(PermissionKind.EnableAllFolders, true);
        limited.SetPreference(PreferenceKind.BlockedMediaFolders, [blocked.Id]);
        await users.UpdateUserAsync(limited);
        Assert.Equal(allowed.Id, Assert.Single((await fixture.Query("UserViews", client)).Items).Id);
        await fixture.AssertNoCatalogImport();
    }

    private static BaseItemKind ExpectedKind(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" or ".vob" => BaseItemKind.Video,
            ".m4a" => BaseItemKind.Audio,
            ".m4b" => BaseItemKind.AudioBook,
            ".png" => BaseItemKind.Photo,
            _ => BaseItemKind.Book
        };

    private static (string Path, string Hash, DateTime Modified)[] Snapshot(string path)
        => Directory.GetFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(file => (file, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), File.GetLastWriteTimeUtc(file))).ToArray();

    private static Task<byte[]> Sample(string route)
        => File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample" + (route == "Audio" ? ".m4b" : ".mp4")), TestContext.Current.CancellationToken);

    private static void RequireEncoder()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JIGGLEFIN_TEST_FFMPEG")))
        {
            throw SkipException.ForSkip("Set JIGGLEFIN_TEST_FFMPEG to verify real selected-file playback.");
        }
    }
}
