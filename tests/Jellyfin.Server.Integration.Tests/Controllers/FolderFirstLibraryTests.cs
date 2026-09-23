using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Jellyfin.Api.Models.LibraryStructureDto;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class FolderFirstLibraryTests
{
    [Fact]
    public async Task MusicAlbum_WithOtherPhysicalSubfolder_RemainsBrowseable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-music-subfolder-" + Guid.NewGuid().ToString("N"));
        var mixedFolder = Path.Combine(testRoot, "Action", "Mixed Album");
        var bonusFolder = Path.Combine(mixedFolder, "Bonus Material");
        var discFolder = Path.Combine(testRoot, "Action", "Multi Disc Album", "Disc 1");
        Directory.CreateDirectory(bonusFolder);
        Directory.CreateDirectory(discFolder);
        await File.WriteAllBytesAsync(Path.Combine(mixedFolder, "Track 01.mp3"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(bonusFolder, "Bonus Track.mp3"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(discFolder, "Track 01.mp3"), [], TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin music subfolder " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=music&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=music", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var rootItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(rootItems);
            var action = Assert.Single(rootItems.Items, item => item.Name == "Action");
            var actionItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(actionItems);
            var mixed = Assert.Single(actionItems.Items, item => item.Name == "Mixed Album");
            Assert.Equal(BaseItemKind.Folder, mixed.Type);
            var multiDiscAlbum = Assert.Single(actionItems.Items, item => item.Name == "Multi Disc Album");
            Assert.Equal(BaseItemKind.MusicAlbum, multiDiscAlbum.Type);

            var children = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={mixed.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(children);
            Assert.Single(children.Items, item => item.Type == BaseItemKind.Audio);
            var bonus = Assert.Single(children.Items, item => item.Name == "Bonus Material");
            Assert.True(bonus.IsFolder);
            var bonusChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={bonus.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(bonusChildren);
            Assert.Single(bonusChildren.Items, item => item.Type == BaseItemKind.Audio);
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task BookAndAudioBook_WithChildFolders_KeepAllPhysicalEntriesBrowseable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-book-subfolders-" + Guid.NewGuid().ToString("N"));
        var categoryFolder = Path.Combine(testRoot, "Action");
        var bookFolder = Path.Combine(categoryFolder, "Book Collection");
        var audioBookFolder = Path.Combine(categoryFolder, "Audio Collection");
        var dualFormatFolder = Path.Combine(categoryFolder, "Dual Format Title");
        Directory.CreateDirectory(Path.Combine(bookFolder, "Bonus Books"));
        Directory.CreateDirectory(Path.Combine(audioBookFolder, "Bonus Audio"));
        Directory.CreateDirectory(dualFormatFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(bookFolder, "First Book.pdf"),
            [],
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(bookFolder, "Bonus Books", "Second Book.pdf"),
            [],
            TestContext.Current.CancellationToken);
        var audiobookBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.m4b"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(audioBookFolder, "First Audio.m4b"),
            audiobookBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(audioBookFolder, "Bonus Audio", "Second Audio.m4b"),
            audiobookBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(dualFormatFolder, "Dual Format Title.epub"),
            [],
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(dualFormatFolder, "Dual Format Title.m4b"),
            audiobookBytes,
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin book subfolders " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=books&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=books", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var rootItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(rootItems);
            var action = Assert.Single(rootItems.Items, item => item.Name == "Action");
            Assert.Equal(BaseItemKind.Folder, action.Type);

            var collections = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(collections);
            foreach (var (folderName, fileKind, bonusName) in new[]
            {
                ("Book Collection", BaseItemKind.Book, "Bonus Books"),
                ("Audio Collection", BaseItemKind.AudioBook, "Bonus Audio")
            })
            {
                var folder = Assert.Single(collections.Items, item => item.Name == folderName);
                Assert.Equal(BaseItemKind.Folder, folder.Type);
                var children = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={folder.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(children);
                var mediaFile = Assert.Single(children.Items, item => item.Type == fileKind);
                if (fileKind == BaseItemKind.AudioBook)
                {
                    using var streamResponse = await client.GetAsync(
                        $"Audio/{mediaFile.Id}/stream?static=true", TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
                    Assert.Equal(audiobookBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
                }

                var bonus = Assert.Single(children.Items, item => item.Name == bonusName);
                Assert.Equal(BaseItemKind.Folder, bonus.Type);
                var bonusChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={bonus.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(bonusChildren);
                Assert.Single(bonusChildren.Items, item => item.Type == fileKind);
            }

            var dualFormat = Assert.Single(collections.Items, item => item.Name == "Dual Format Title");
            Assert.Equal(BaseItemKind.Folder, dualFormat.Type);
            var dualFormatItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={dualFormat.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(dualFormatItems);
            Assert.Single(dualFormatItems.Items, item => item.Type == BaseItemKind.Book);
            Assert.Single(dualFormatItems.Items, item => item.Type == BaseItemKind.AudioBook);
        }
        finally
        {
            if (created)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }

    [Fact]
    public async Task LibraryViews_ListPhysicalGroupsBeforeMedia()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-folder-view-" + Guid.NewGuid().ToString("N"));
        var libraries = new[]
        {
            (CollectionType: "movies", ItemFolder: "Example Movie (2020)", FileName: "Example Movie (2020).mkv", ExpectedKind: BaseItemKind.Movie),
            (CollectionType: "tvshows", ItemFolder: "Example Show", FileName: "Example Show - S01E01.mkv", ExpectedKind: BaseItemKind.Series),
            (CollectionType: "books", ItemFolder: "Example Book", FileName: "Example Book.pdf", ExpectedKind: BaseItemKind.Book),
            (CollectionType: "books", ItemFolder: "Example Audio Book", FileName: "Example Audio Book.m4b", ExpectedKind: BaseItemKind.AudioBook),
            (CollectionType: "music", ItemFolder: "Example Album", FileName: "Track 01.mp3", ExpectedKind: BaseItemKind.MusicAlbum),
            (CollectionType: "homevideos", ItemFolder: "Home Clip", FileName: "Home Clip.mp4", ExpectedKind: BaseItemKind.Video),
            (CollectionType: "musicvideos", ItemFolder: "Music Clip", FileName: "Music Clip.mp4", ExpectedKind: BaseItemKind.MusicVideo)
        };
        var audiobookBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.m4b"),
            TestContext.Current.CancellationToken);
        foreach (var library in libraries)
        {
            var mediaFolder = Path.Combine(testRoot, library.ExpectedKind.ToString(), "Action", library.ItemFolder);
            Directory.CreateDirectory(mediaFolder);
            await File.WriteAllBytesAsync(
                Path.Combine(mediaFolder, library.FileName),
                library.ExpectedKind == BaseItemKind.AudioBook ? audiobookBytes : [],
                TestContext.Current.CancellationToken);
        }

        var looseEpisodeFolder = Path.Combine(testRoot, nameof(BaseItemKind.Series), "Drama");
        Directory.CreateDirectory(looseEpisodeFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(looseEpisodeFolder, "Another Show - S01E01.mkv"),
            [],
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(testRoot, nameof(BaseItemKind.Video), "Action", "Home Clip", "Snapshot.png"),
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl5aZkAAAAASUVORK5CYII="),
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var createdLibraries = new List<string>();

        try
        {
            foreach (var library in libraries)
            {
                var libraryName = "Jigglefin" + library.ExpectedKind;
                var mediaRoot = Path.Combine(testRoot, library.ExpectedKind.ToString());
                var createUrl = $"Library/VirtualFolders?name={libraryName}&collectionType={library.CollectionType}&paths={Uri.EscapeDataString(mediaRoot)}&refreshLibrary=false";
                using var createResponse = await client.PostAsJsonAsync(
                    createUrl,
                    new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
                createdLibraries.Add(libraryName);
            }

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(
                new Progress<double>(),
                TestContext.Current.CancellationToken);

            var defaultViews = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(defaultViews);
            foreach (var library in libraries)
            {
                var defaultView = Assert.Single(defaultViews.Items, item => item.Name == "Jigglefin" + library.ExpectedKind);
                Assert.Equal(BaseItemKind.Folder, defaultView.Type);
                Assert.Null(defaultView.CollectionType);
                Assert.True(defaultView.IsFolder);
            }

            foreach (var library in libraries)
            {
                var libraryName = "Jigglefin" + library.ExpectedKind;
                var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"UserViews?presetViews={library.CollectionType}",
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(views);
                var libraryView = Assert.Single(views.Items, item => item.Name == libraryName);
                Assert.Equal(BaseItemKind.Folder, libraryView.Type);
                Assert.Null(libraryView.CollectionType);
                Assert.True(libraryView.IsFolder);

                var firstLevel = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={libraryView.Id}",
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(firstLevel);
                Assert.True(
                    firstLevel.Items.Any(item => item.Name == "Action"),
                    $"No Action folder for {library.CollectionType}; first level: {string.Join(", ", firstLevel.Items.Select(item => item.Name + ":" + item.Type))}");
                var actionFolder = Assert.Single(firstLevel.Items, item => item.Name == "Action");
                Assert.Equal(BaseItemKind.Folder, actionFolder.Type);

                if (library.ExpectedKind == BaseItemKind.Series)
                {
                    var dramaFolder = Assert.Single(firstLevel.Items, item => item.Name == "Drama");
                    Assert.Equal(BaseItemKind.Folder, dramaFolder.Type);
                    var looseEpisodes = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                        $"Items?parentId={dramaFolder.Id}",
                        JsonDefaults.Options,
                        TestContext.Current.CancellationToken);
                    Assert.NotNull(looseEpisodes);
                    Assert.Single(looseEpisodes.Items, item => item.Type == BaseItemKind.Episode);
                }

                var secondLevel = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={actionFolder.Id}",
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(secondLevel);
                var mediaEntry = Assert.Single(secondLevel.Items, item => item.Name.Contains(library.ItemFolder.Split('(')[0].Trim(), StringComparison.Ordinal));
                if ((library.CollectionType is "homevideos" or "musicvideos")
                    && (mediaEntry.Type is BaseItemKind.Folder or BaseItemKind.PhotoAlbum))
                {
                    var files = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                        $"Items?parentId={mediaEntry.Id}",
                        JsonDefaults.Options,
                        TestContext.Current.CancellationToken);
                    Assert.NotNull(files);
                    Assert.Single(files.Items, item => item.Type == library.ExpectedKind);
                    if (library.CollectionType == "homevideos")
                    {
                        Assert.Single(files.Items, item => item.Type == BaseItemKind.Photo);
                    }
                }
                else
                {
                    Assert.Equal(library.ExpectedKind, mediaEntry.Type);
                }

                if (library.ExpectedKind == BaseItemKind.AudioBook)
                {
                    using var streamResponse = await client.GetAsync(
                        $"Audio/{mediaEntry.Id}/stream?static=true",
                        TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
                    Assert.Equal(audiobookBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

                    using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"Audio/{mediaEntry.Id}/stream?static=true");
                    rangeRequest.Headers.Range = new RangeHeaderValue(100, 199);
                    using var rangeResponse = await client.SendAsync(rangeRequest, TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
                    Assert.Equal(audiobookBytes[100..200], await rangeResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
                }
            }
        }
        finally
        {
            foreach (var libraryName in createdLibraries)
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={libraryName}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
            }

            Directory.Delete(testRoot, true);
        }
    }
}
