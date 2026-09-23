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
    public async Task HomeVideoDiscRip_WithPhoto_KeepsBothPhysicalEntriesBrowseable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-homevideo-disc-photo-" + Guid.NewGuid().ToString("N"));
        var featureFolder = Path.Combine(testRoot, "Family", "Disc Feature");
        var videoTsFolder = Path.Combine(featureFolder, "VIDEO_TS");
        Directory.CreateDirectory(videoTsFolder);
        await File.WriteAllBytesAsync(Path.Combine(videoTsFolder, "VIDEO_TS.IFO"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(videoTsFolder, "VTS_01_1.VOB"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(featureFolder, "Snapshot.png"),
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl5aZkAAAAASUVORK5CYII="),
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin homevideo disc photo " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=homevideos&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=homevideos", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var rootItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(rootItems);
            var family = Assert.Single(rootItems.Items, item => item.Name == "Family");
            Assert.Equal(BaseItemKind.Folder, family.Type);
            var familyItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={family.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(familyItems);
            var feature = Assert.Single(familyItems.Items, item => item.Name == "Disc Feature");
            Assert.True(feature.IsFolder);
            var featureItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={feature.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(featureItems);
            Assert.Single(featureItems.Items, item => item.Type == BaseItemKind.Video);
            Assert.Single(featureItems.Items, item => item.Type == BaseItemKind.Photo);
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
    public async Task MultiChapterAudioBook_KeepsEveryPhysicalAudioFileBrowseable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-audiobook-chapters-" + Guid.NewGuid().ToString("N"));
        var bookFolder = Path.Combine(testRoot, "Example Author", "Example Book");
        Directory.CreateDirectory(bookFolder);
        var audioBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.m4b"),
            TestContext.Current.CancellationToken);
        foreach (var chapterName in new[] { "Chapter 1.m4b", "Chapter 2.m4b", "Extra.m4b" })
        {
            await File.WriteAllBytesAsync(Path.Combine(bookFolder, chapterName), audioBytes, TestContext.Current.CancellationToken);
        }

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin audiobook chapters " + Guid.NewGuid().ToString("N");
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
            var authors = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(authors);
            var author = Assert.Single(authors.Items, item => item.Name == "Example Author");
            Assert.Equal(BaseItemKind.Folder, author.Type);
            var books = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={author.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(books);
            var book = Assert.Single(books.Items, item => item.Name == "Example Book");
            Assert.Equal(BaseItemKind.Folder, book.Type);
            var chapters = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={book.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(chapters);
            Assert.Equal(3, chapters.Items.Count);
            Assert.All(chapters.Items, item => Assert.Equal(BaseItemKind.AudioBook, item.Type));
            Assert.Contains(chapters.Items, item => item.Name == "Chapter 1");
            Assert.Contains(chapters.Items, item => item.Name == "Extra");
            var secondChapter = Assert.Single(chapters.Items, item => item.Name == "Chapter 2");
            using var streamResponse = await client.GetAsync(
                $"Audio/{secondChapter.Id}/stream?static=true", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            Assert.Equal(audioBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
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
    public async Task Series_WithUnnumberedVideoSubfolder_KeepsPhysicalFolderBrowseable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-series-subfolder-" + Guid.NewGuid().ToString("N"));
        var seriesFolder = Path.Combine(testRoot, "Drama", "Example Show");
        var seasonFolder = Path.Combine(seriesFolder, "Season 1");
        var seasonBonusFolder = Path.Combine(seasonFolder, "Season Bonus");
        var bonusFolder = Path.Combine(seriesFolder, "Bonus Collection");
        Directory.CreateDirectory(seasonFolder);
        Directory.CreateDirectory(seasonBonusFolder);
        Directory.CreateDirectory(bonusFolder);
        var videoBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(seasonFolder, "Example Show - S01E01.mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(bonusFolder, "Bonus Clip.mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(seasonBonusFolder, "Season Bonus Clip.mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin series subfolder " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=tvshows&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=tvshows", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var drama = Assert.Single(groups.Items, item => item.Name == "Drama");
            var seriesItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={drama.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(seriesItems);
            var series = Assert.Single(seriesItems.Items, item => item.Name == "Example Show");
            Assert.Equal(BaseItemKind.Series, series.Type);
            var children = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={series.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(children);
            var season = Assert.Single(children.Items, item => item.Name == "Season 1");
            Assert.Equal(BaseItemKind.Season, season.Type);
            var seasonChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={season.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(seasonChildren);
            Assert.Single(seasonChildren.Items, item => item.Type == BaseItemKind.Episode);
            var seasonBonus = Assert.Single(seasonChildren.Items, item => item.Name == "Season Bonus");
            Assert.Equal(BaseItemKind.Folder, seasonBonus.Type);
            var seasonBonusChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={seasonBonus.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(seasonBonusChildren);
            var seasonBonusEpisode = Assert.Single(seasonBonusChildren.Items, item => item.Type == BaseItemKind.Episode);
            var episodeItem = libraryManager.GetItemById(seasonBonusEpisode.Id);
            Assert.Equal(1, episodeItem?.ParentIndexNumber);
            var bonus = Assert.Single(children.Items, item => item.Name == "Bonus Collection");
            Assert.Equal(BaseItemKind.Folder, bonus.Type);
            var bonusChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={bonus.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(bonusChildren);
            var bonusEpisode = Assert.Single(bonusChildren.Items, item => item.Type == BaseItemKind.Episode);
            using var streamResponse = await client.GetAsync(
                $"Videos/{bonusEpisode.Id}/stream?static=true", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            Assert.Equal(videoBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            var seasons = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Shows/{series.Id}/Seasons", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(seasons);
            Assert.Contains(seasons.Items, item => item.Name == "Season 1");
            Assert.All(seasons.Items, item => Assert.Equal(BaseItemKind.Season, item.Type));
            var groupedEpisodes = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Shows/{series.Id}/Episodes?seasonId={season.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groupedEpisodes);
            Assert.Contains(groupedEpisodes.Items, item => item.Name == "Season Bonus Clip");
            var unknownSeason = Assert.Single(seasons.Items, item => item.Name == "Season Unknown");
            var unknownEpisodes = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={unknownSeason.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(unknownEpisodes);
            Assert.Single(unknownEpisodes.Items, item => item.Type == BaseItemKind.Episode);
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
    public async Task MovieDiscRips_PreservePhysicalCategoryAndSiblingFolders()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-movie-disc-subfolders-" + Guid.NewGuid().ToString("N"));
        var categoryFolder = Path.Combine(testRoot, "Action");
        var directRipFolder = Path.Combine(categoryFolder, "Disc Feature");
        var directRipVideoTs = Path.Combine(directRipFolder, "VIDEO_TS");
        var stackedRipFolder = Path.Combine(categoryFolder, "Disc Collection");
        var stackedRipVideoTs = Path.Combine(stackedRipFolder, "Disc 1", "VIDEO_TS");
        var standaloneRipVideoTs = Path.Combine(categoryFolder, "Standalone DVD", "VIDEO_TS");
        var soloRipVideoTs = Path.Combine(testRoot, "Solo Category", "Only DVD", "VIDEO_TS");
        var singleDiscVideoTs = Path.Combine(testRoot, "Single Disc Category", "Disc 1", "VIDEO_TS");
        Directory.CreateDirectory(directRipVideoTs);
        Directory.CreateDirectory(stackedRipVideoTs);
        Directory.CreateDirectory(standaloneRipVideoTs);
        Directory.CreateDirectory(soloRipVideoTs);
        Directory.CreateDirectory(singleDiscVideoTs);
        Directory.CreateDirectory(Path.Combine(directRipFolder, "Bonus Film"));
        Directory.CreateDirectory(Path.Combine(stackedRipFolder, "Other Film"));
        foreach (var videoTsFolder in new[] { directRipVideoTs, stackedRipVideoTs, standaloneRipVideoTs, soloRipVideoTs, singleDiscVideoTs })
        {
            await File.WriteAllBytesAsync(Path.Combine(videoTsFolder, "VIDEO_TS.IFO"), [], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(videoTsFolder, "VTS_01_1.VOB"), [], TestContext.Current.CancellationToken);
        }

        await File.WriteAllBytesAsync(Path.Combine(directRipFolder, "Bonus Film", "Bonus Film.mp4"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(directRipFolder, "Disc Feature.mp4"), [], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(stackedRipFolder, "Other Film", "Other Film.mp4"), [], TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin movie disc subfolders " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType=movies&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                "UserViews?presetViews=movies", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var rootItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(rootItems);
            var action = Assert.Single(rootItems.Items, item => item.Name == "Action");
            var soloCategory = Assert.Single(rootItems.Items, item => item.Name == "Solo Category");
            Assert.Equal(BaseItemKind.Folder, soloCategory.Type);
            var soloChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={soloCategory.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(soloChildren);
            Assert.Equal(BaseItemKind.Movie, Assert.Single(soloChildren.Items, item => item.Name == "Only DVD").Type);
            var singleDiscCategory = Assert.Single(rootItems.Items, item => item.Name == "Single Disc Category");
            Assert.Equal(BaseItemKind.Folder, singleDiscCategory.Type);
            var singleDiscChildren = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={singleDiscCategory.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(singleDiscChildren);
            Assert.Equal(BaseItemKind.Movie, Assert.Single(singleDiscChildren.Items, item => item.Name == "Disc 1").Type);
            var collections = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(collections);
            Assert.Equal(BaseItemKind.Movie, Assert.Single(collections.Items, item => item.Name == "Standalone DVD").Type);

            foreach (var (folderName, siblingName) in new[]
            {
                ("Disc Feature", "Bonus Film"),
                ("Disc Collection", "Other Film")
            })
            {
                var folder = Assert.Single(collections.Items, item => item.Name == folderName);
                Assert.Equal(BaseItemKind.Folder, folder.Type);
                var children = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                    $"Items?parentId={folder.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
                Assert.NotNull(children);
                Assert.Single(children.Items, item => item.Name == siblingName);
                var disc = Assert.Single(children.Items, item => item.Name == (folderName == "Disc Feature" ? "VIDEO_TS" : "Disc 1"));
                Assert.Equal(BaseItemKind.Movie, disc.Type);
                if (folderName == "Disc Feature")
                {
                    Assert.Equal(BaseItemKind.Movie, Assert.Single(children.Items, item => item.Name == "Disc Feature").Type);
                }
            }
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
            var albumItem = Assert.IsType<MediaBrowser.Controller.Entities.Audio.MusicAlbum>(libraryManager.GetItemById(multiDiscAlbum.Id));
            Assert.Contains(albumItem.Children, item => item.Name == "Disc 1");
            var discItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={multiDiscAlbum.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(discItems);
            var disc = Assert.Single(discItems.Items, item => item.Name == "Disc 1");
            Assert.True(disc.IsFolder);
            var discTracks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={disc.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(discTracks);
            Assert.Single(discTracks.Items, item => item.Type == BaseItemKind.Audio);
            var filteredTracks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={multiDiscAlbum.Id}&includeItemTypes=Audio", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(filteredTracks);
            Assert.Single(filteredTracks.Items, item => item.Type == BaseItemKind.Audio);
            var albumDetailsTracks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={multiDiscAlbum.Id}&sortBy=ParentIndexNumber,IndexNumber,SortName",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(albumDetailsTracks);
            Assert.Single(albumDetailsTracks.Items, item => item.Type == BaseItemKind.Audio);

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
        var snapshotBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl5aZkAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(
            Path.Combine(testRoot, nameof(BaseItemKind.Video), "Action", "Home Clip", "Snapshot.png"),
            snapshotBytes,
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
                        var photo = Assert.Single(files.Items, item => item.Type == BaseItemKind.Photo);
                        using var imageResponse = await client.GetAsync(
                            $"Items/{photo.Id}/Images/Primary", TestContext.Current.CancellationToken);
                        Assert.Equal(HttpStatusCode.OK, imageResponse.StatusCode);
                        Assert.Equal("image/png", imageResponse.Content.Headers.ContentType?.MediaType);
                        var imageBytes = await imageResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
                        Assert.True(imageBytes.Length > 8);
                        Assert.True(imageBytes.AsSpan(0, 8).SequenceEqual(snapshotBytes.AsSpan(0, 8)));
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
