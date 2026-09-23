using System;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Text;
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

public sealed class FolderFirstRescanTests
{
    [Theory]
    [InlineData("movies", "Named Item.mp4", BaseItemKind.Movie)]
    [InlineData("tvshows", "Named Item - S01E01.mp4", BaseItemKind.Series)]
    [InlineData("books", "Named Item.pdf", BaseItemKind.Book)]
    [InlineData("music", "Track 01.mp3", BaseItemKind.MusicAlbum)]
    public async Task RemovingAndRestoringLastMediaFile_ChangesPhysicalFolderKind(
        string collectionType,
        string mediaFileName,
        BaseItemKind initialKind)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-empty-after-rescan-" + Guid.NewGuid().ToString("N"));
        var mediaFolder = Path.Combine(testRoot, "Action", "Named Item");
        Directory.CreateDirectory(mediaFolder);
        var mediaPath = Path.Combine(mediaFolder, mediaFileName);
        await File.WriteAllBytesAsync(mediaPath, [], TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin empty after rescan " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType={collectionType}&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"UserViews?presetViews={collectionType}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var action = Assert.Single(groups.Items, item => item.Name == "Action");
            var initialItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(initialItems);
            Assert.Equal(initialKind, Assert.Single(initialItems.Items).Type);

            File.Delete(mediaPath);
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var remainingItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}&fields=Path", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(remainingItems);
            var emptyFolder = Assert.Single(remainingItems.Items);
            Assert.Equal("Named Item", emptyFolder.Name);
            Assert.Equal(BaseItemKind.Folder, emptyFolder.Type);
            Assert.Equal(mediaFolder, emptyFolder.Path);
            var children = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={emptyFolder.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(children);
            Assert.Empty(children.Items);

            await File.WriteAllBytesAsync(mediaPath, [], TestContext.Current.CancellationToken);
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var restoredItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(restoredItems);
            Assert.Equal(initialKind, Assert.Single(restoredItems.Items).Type);
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

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    [Fact]
    public async Task RenamedAndRemovedSeries_UpdatePhysicalBrowseAndEpisodeStream()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-series-rescan-" + Guid.NewGuid().ToString("N"));
        var categoryFolder = Path.Combine(testRoot, "Drama");
        var originalSeries = Path.Combine(categoryFolder, "Original Show");
        var renamedSeries = Path.Combine(categoryFolder, "Renamed Show");
        var originalSeason = Path.Combine(originalSeries, "Season 1");
        Directory.CreateDirectory(originalSeason);
        var episodeBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(originalSeason, "Original Show - S01E01.mp4"),
            episodeBytes,
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin series rescan test " + Guid.NewGuid().ToString("N");
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
            var category = Assert.Single(groups.Items, item => item.Name == "Drama");
            var originalShows = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={category.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(originalShows);
            var original = Assert.Single(originalShows.Items);
            Assert.Equal("Original Show", original.Name);
            Assert.Equal(BaseItemKind.Series, original.Type);

            Directory.Move(originalSeries, renamedSeries);
            File.Move(
                Path.Combine(renamedSeries, "Season 1", "Original Show - S01E01.mp4"),
                Path.Combine(renamedSeries, "Season 1", "Renamed Show - S01E01.mp4"));
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var renamedShows = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={category.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(renamedShows);
            var renamed = Assert.Single(renamedShows.Items);
            Assert.Equal("Renamed Show", renamed.Name);
            Assert.Equal(BaseItemKind.Series, renamed.Type);
            Assert.NotEqual(original.Id, renamed.Id);
            var seasons = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={renamed.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(seasons);
            var season = Assert.Single(seasons.Items);
            Assert.Equal("Season 1", season.Name);
            Assert.Equal(BaseItemKind.Season, season.Type);
            var episodes = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={season.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(episodes);
            var episode = Assert.Single(episodes.Items);
            Assert.Equal(BaseItemKind.Episode, episode.Type);
            using var streamResponse = await client.GetAsync(
                $"Videos/{episode.Id}/stream?static=true", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            Assert.Equal(episodeBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            Directory.Delete(renamedSeries, true);
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var remainingEpisodes = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}&includeItemTypes=Episode&recursive=true",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(remainingEpisodes);
            Assert.Empty(remainingEpisodes.Items);
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

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    [Fact]
    public async Task RenamedAndRemovedMusicAlbums_UpdatePhysicalBrowseAndTrackStream()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-music-rescan-" + Guid.NewGuid().ToString("N"));
        var categoryFolder = Path.Combine(testRoot, "Action");
        var originalAlbum = Path.Combine(categoryFolder, "Original Album");
        var renamedAlbum = Path.Combine(categoryFolder, "Renamed Album");
        Directory.CreateDirectory(originalAlbum);
        var audioBytes = CreateSilentWave();
        await File.WriteAllBytesAsync(
            Path.Combine(originalAlbum, "Track 01.wav"), audioBytes, TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin music rescan test " + Guid.NewGuid().ToString("N");
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
            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var category = Assert.Single(groups.Items, item => item.Name == "Action");
            var originalAlbums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={category.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(originalAlbums);
            var original = Assert.Single(originalAlbums.Items);
            Assert.Equal("Original Album", original.Name);
            Assert.Equal(BaseItemKind.MusicAlbum, original.Type);

            Directory.Move(originalAlbum, renamedAlbum);
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var renamedAlbums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={category.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(renamedAlbums);
            var renamed = Assert.Single(renamedAlbums.Items);
            Assert.Equal("Renamed Album", renamed.Name);
            Assert.Equal(BaseItemKind.MusicAlbum, renamed.Type);
            Assert.NotEqual(original.Id, renamed.Id);
            var tracks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={renamed.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(tracks);
            var track = Assert.Single(tracks.Items);
            Assert.Equal(BaseItemKind.Audio, track.Type);
            using var streamResponse = await client.GetAsync(
                $"Audio/{track.Id}/stream?static=true", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            Assert.Equal(audioBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            Directory.Delete(renamedAlbum, true);
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var remainingTracks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}&includeItemTypes=Audio&recursive=true",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(remainingTracks);
            Assert.Empty(remainingTracks.Items);
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

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    [Theory]
    [InlineData("movies", ".mp4", BaseItemKind.Movie, "Videos")]
    [InlineData("books", ".m4b", BaseItemKind.AudioBook, "Audio")]
    public async Task RenamedAndRemovedMediaFolders_UpdatePhysicalBrowseAndStreamPaths(
        string collectionType,
        string extension,
        BaseItemKind expectedKind,
        string streamPrefix)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-rescan-" + Guid.NewGuid().ToString("N"));
        var categoryFolder = Path.Combine(testRoot, "Action");
        var originalFolder = Path.Combine(categoryFolder, "Original Item");
        var renamedFolder = Path.Combine(categoryFolder, "Renamed Item");
        Directory.CreateDirectory(originalFolder);
        var originalPath = Path.Combine(originalFolder, "Original Item" + extension);
        var mediaBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample" + extension),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(originalPath, mediaBytes, TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin rescan test " + Guid.NewGuid().ToString("N");
        var created = false;

        try
        {
            using var createResponse = await client.PostAsJsonAsync(
                $"Library/VirtualFolders?name={Uri.EscapeDataString(libraryName)}&collectionType={collectionType}&paths={Uri.EscapeDataString(testRoot)}&refreshLibrary=false",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions() },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            created = true;

            var libraryManager = (LibraryManager)factory.Services.GetRequiredService<ILibraryManager>();
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var views = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"UserViews?presetViews={collectionType}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var category = Assert.Single(groups.Items, item => item.Name == "Action");
            var originalItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={category.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(originalItems);
            var original = Assert.Single(originalItems.Items);
            Assert.Equal("Original Item", original.Name);
            Assert.Equal(expectedKind, original.Type);

            Directory.Move(originalFolder, renamedFolder);
            var renamedPath = Path.Combine(renamedFolder, "Renamed Item" + extension);
            File.Move(Path.Combine(renamedFolder, "Original Item" + extension), renamedPath);
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var renamedItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={category.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(renamedItems);
            var renamed = Assert.Single(renamedItems.Items);
            Assert.Equal("Renamed Item", renamed.Name);
            Assert.Equal(expectedKind, renamed.Type);
            Assert.NotEqual(original.Id, renamed.Id);
            using var streamResponse = await client.GetAsync(
                $"{streamPrefix}/{renamed.Id}/stream?static=true", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            Assert.Equal(mediaBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            Directory.Delete(renamedFolder, true);
            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);

            var remainingMedia = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}&includeItemTypes={expectedKind}&recursive=true",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(remainingMedia);
            Assert.Empty(remainingMedia.Items);
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

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    private static byte[] CreateSilentWave()
    {
        const int sampleRate = 8000;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + sampleRate);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(sampleRate);
            writer.Write(sampleRate);
            writer.Write((short)1);
            writer.Write((short)8);
            writer.Write("data"u8);
            writer.Write(sampleRate);
            for (var i = 0; i < sampleRate; i++)
            {
                writer.Write((byte)128);
            }
        }

        return stream.ToArray();
    }
}
