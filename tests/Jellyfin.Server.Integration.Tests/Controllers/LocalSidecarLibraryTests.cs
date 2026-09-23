using System;
using System.IO;
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

public sealed class LocalSidecarLibraryTests
{
    [Fact]
    public async Task MusicNfo_ProvidesLocalArtistAndAlbumMetadataThroughPhysicalFolders()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-music-nfo-" + Guid.NewGuid().ToString("N"));
        var artistFolder = Path.Combine(testRoot, "Genres", "Physical Artist");
        var albumFolder = Path.Combine(artistFolder, "Physical Album");
        Directory.CreateDirectory(albumFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(albumFolder, "Track 01.mp3"),
            [],
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(artistFolder, "artist.nfo"),
            "<artist><name>Local NFO Artist</name><genre>Jazz</genre></artist>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(albumFolder, "album.nfo"),
            "<album><title>Local NFO Album</title><year>2022</year><plot>Local album description.</plot></album>",
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin music NFO test " + Guid.NewGuid().ToString("N");
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
            Assert.Equal(BaseItemKind.Folder, library.Type);
            var rootItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(rootItems);
            var genres = Assert.Single(rootItems.Items, item => item.Name == "Genres");
            Assert.Equal(BaseItemKind.Folder, genres.Type);
            var artists = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={genres.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(artists);
            var artist = Assert.Single(artists.Items, item => item.Name == "Local NFO Artist");
            Assert.Equal(BaseItemKind.MusicArtist, artist.Type);
            var albums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={artist.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(albums);
            var album = Assert.Single(albums.Items, item => item.Name == "Local NFO Album");
            Assert.Equal(BaseItemKind.MusicAlbum, album.Type);
            Assert.Equal(2022, album.ProductionYear);
            var details = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{album.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("Local album description.", details?.Overview);
            var tracks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={album.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(tracks);
            Assert.Single(tracks.Items, item => item.Type == BaseItemKind.Audio);

            await libraryManager.ValidateMediaLibraryInternal(new Progress<double>(), TestContext.Current.CancellationToken);
            var refreshedAlbums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={artist.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(refreshedAlbums);
            Assert.Single(refreshedAlbums.Items, item => item.Name == "Local NFO Album");
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
    public async Task EmbySeriesXml_ProvidesClientMetadataWithoutHidingPhysicalGroups()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-series-xml-" + Guid.NewGuid().ToString("N"));
        var seriesFolder = Path.Combine(testRoot, "Drama", "Example Show");
        Directory.CreateDirectory(seriesFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(seriesFolder, "Example Show - S01E01.mp4"),
            await File.ReadAllBytesAsync(
                Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(seriesFolder, "series.xml"),
            "<Series><LocalTitle>Local XML Series</LocalTitle><ProductionYear>2020</ProductionYear><Overview>From the series XML.</Overview></Series>",
            TestContext.Current.CancellationToken);
        var precedenceFolder = Path.Combine(testRoot, "Drama", "Both Sources");
        Directory.CreateDirectory(precedenceFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(precedenceFolder, "Both Sources - S01E01.mp4"),
            await File.ReadAllBytesAsync(
                Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(precedenceFolder, "series.xml"),
            "<Series><LocalTitle>Secondary Series XML</LocalTitle></Series>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(precedenceFolder, "tvshow.nfo"),
            "<tvshow><title>Preferred Series NFO</title></tvshow>",
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin series XML test " + Guid.NewGuid().ToString("N");
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
            Assert.Equal(BaseItemKind.Folder, library.Type);

            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var drama = Assert.Single(groups.Items, item => item.Name == "Drama");
            Assert.Equal(BaseItemKind.Folder, drama.Type);

            var seriesItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={drama.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(seriesItems);
            Assert.Equal(2, seriesItems.Items.Count);
            var series = Assert.Single(seriesItems.Items, item => item.Name == "Local XML Series");
            var preferredNfo = Assert.Single(seriesItems.Items, item => item.Name == "Preferred Series NFO");
            Assert.Equal(BaseItemKind.Series, series.Type);
            Assert.Equal(BaseItemKind.Series, preferredNfo.Type);
            Assert.Equal(2020, series.ProductionYear);

            var details = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{series.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("From the series XML.", details?.Overview);
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
    public async Task EmbyMovieXml_ProvidesClientMetadataForDedicatedAndLooseMovies()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-xml-sidecar-" + Guid.NewGuid().ToString("N"));
        var categoryFolder = Path.Combine(testRoot, "Action");
        var dedicatedFolder = Path.Combine(categoryFolder, "Dedicated Movie (2021)");
        Directory.CreateDirectory(dedicatedFolder);
        var videoBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(dedicatedFolder, "Dedicated Movie (2021).mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(dedicatedFolder, "movie.xml"),
            "<Item><LocalTitle>Dedicated XML Title</LocalTitle><ProductionYear>2021</ProductionYear><Overview>Dedicated XML overview.</Overview></Item>",
            TestContext.Current.CancellationToken);
        var alternateNameFolder = Path.Combine(categoryFolder, "Emby Folder Name (2024)");
        Directory.CreateDirectory(alternateNameFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(alternateNameFolder, "alternate-name.mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(alternateNameFolder, "movie.xml"),
            "<Item><LocalTitle>Different File XML Title</LocalTitle><ProductionYear>2024</ProductionYear></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(categoryFolder, "Loose Movie (2022).mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(categoryFolder, "Loose Movie (2022).xml"),
            "<Item><LocalTitle>Loose XML Title</LocalTitle><ProductionYear>2022</ProductionYear><Overview>Loose XML overview.</Overview></Item>",
            TestContext.Current.CancellationToken);
        var precedenceFolder = Path.Combine(categoryFolder, "Precedence Movie (2023)");
        Directory.CreateDirectory(precedenceFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(precedenceFolder, "Precedence Movie (2023).mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(precedenceFolder, "movie.xml"),
            "<Item><LocalTitle>Secondary XML Title</LocalTitle></Item>",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(precedenceFolder, "movie.nfo"),
            "<movie><title>Preferred NFO Title</title></movie>",
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin XML sidecar test " + Guid.NewGuid().ToString("N");
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
                "UserViews?presetViews=movies",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            Assert.Equal(BaseItemKind.Folder, library.Type);

            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var action = Assert.Single(groups.Items, item => item.Name == "Action");
            Assert.Equal(BaseItemKind.Folder, action.Type);

            var movies = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(movies);
            Assert.Equal(4, movies.Items.Count);
            var dedicated = Assert.Single(movies.Items, item => item.Name == "Dedicated XML Title");
            var alternateName = Assert.Single(movies.Items, item => item.Name == "Different File XML Title");
            var loose = Assert.Single(movies.Items, item => item.Name == "Loose XML Title");
            var preferredNfo = Assert.Single(movies.Items, item => item.Name == "Preferred NFO Title");
            Assert.Equal(BaseItemKind.Movie, dedicated.Type);
            Assert.Equal(BaseItemKind.Movie, alternateName.Type);
            Assert.Equal(BaseItemKind.Movie, loose.Type);
            Assert.Equal(BaseItemKind.Movie, preferredNfo.Type);
            Assert.Equal(2021, dedicated.ProductionYear);
            Assert.Equal(2024, alternateName.ProductionYear);
            Assert.Equal(2022, loose.ProductionYear);

            var dedicatedDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{dedicated.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            var looseDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{loose.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.Equal("Dedicated XML overview.", dedicatedDetails?.Overview);
            Assert.Equal("Loose XML overview.", looseDetails?.Overview);
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
    public async Task SingleLooseMovie_DoesNotReplacePhysicalCategoryFolder()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-loose-movie-" + Guid.NewGuid().ToString("N"));
        var categoryFolder = Path.Combine(testRoot, "Action");
        Directory.CreateDirectory(categoryFolder);
        var matchedMovieFolder = Path.Combine(testRoot, "Comedy", "Matched Movie (2020)");
        Directory.CreateDirectory(matchedMovieFolder);
        var yearInFolder = Path.Combine(testRoot, "Drama", "Year in Folder (2020)");
        Directory.CreateDirectory(yearInFolder);
        var videoBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(categoryFolder, "Loose Movie (2020).mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(matchedMovieFolder, "Matched Movie (2020).mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(yearInFolder, "Year in Folder.mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin loose movie test " + Guid.NewGuid().ToString("N");
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
                "UserViews?presetViews=movies",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            Assert.Equal(BaseItemKind.Folder, library.Type);
            Assert.Null(library.CollectionType);
            Assert.True(library.IsFolder);

            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var action = Assert.Single(groups.Items, item => item.Name == "Action");
            Assert.Equal(BaseItemKind.Folder, action.Type);

            var movies = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(movies);
            Assert.Single(movies.Items, item => item.Type == BaseItemKind.Movie);

            var comedy = Assert.Single(groups.Items, item => item.Name == "Comedy");
            Assert.Equal(BaseItemKind.Folder, comedy.Type);
            var movieFolders = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={comedy.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(movieFolders);
            var matchedMovie = Assert.Single(movieFolders.Items);
            Assert.Equal(BaseItemKind.Movie, matchedMovie.Type);
            Assert.Equal("Matched Movie (2020)", matchedMovie.Name);

            var drama = Assert.Single(groups.Items, item => item.Name == "Drama");
            Assert.Equal(BaseItemKind.Folder, drama.Type);
            var dramaItems = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={drama.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(dramaItems);
            Assert.Equal(BaseItemKind.Movie, Assert.Single(dramaItems.Items).Type);
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
    public async Task MovieNfo_ProvidesClientMetadataWithoutHidingPhysicalGroups()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-sidecar-" + Guid.NewGuid().ToString("N"));
        var movieFolder = Path.Combine(testRoot, "Action", "Example Movie (2020)");
        Directory.CreateDirectory(movieFolder);
        var videoBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(movieFolder, "Example Movie (2020).mp4"),
            videoBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(movieFolder, "movie.nfo"),
            "<movie><title>A Local Sidecar Title</title><year>2020</year><plot>From the local NFO.</plot></movie>",
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin sidecar test " + Guid.NewGuid().ToString("N");
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
                "UserViews?presetViews=movies",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(views);
            var library = Assert.Single(views.Items, item => item.Name == libraryName);
            Assert.Equal(BaseItemKind.Folder, library.Type);
            Assert.Null(library.CollectionType);
            Assert.True(library.IsFolder);

            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var action = Assert.Single(groups.Items, item => item.Name == "Action");
            Assert.Equal(BaseItemKind.Folder, action.Type);

            var movies = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(movies);
            var movie = Assert.Single(movies.Items, item => item.Type == BaseItemKind.Movie);
            Assert.Equal("A Local Sidecar Title", movie.Name);
            Assert.Equal(2020, movie.ProductionYear);

            var movieDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{movie.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(movieDetails);
            Assert.Equal("From the local NFO.", movieDetails.Overview);

            using var streamResponse = await client.GetAsync(
                $"Videos/{movie.Id}/stream?static=true",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            Assert.Equal(videoBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"Videos/{movie.Id}/stream?static=true");
            rangeRequest.Headers.Range = new RangeHeaderValue(100, 199);
            using var rangeResponse = await client.SendAsync(rangeRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
            Assert.Equal(videoBytes[100..200], await rangeResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
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
}
