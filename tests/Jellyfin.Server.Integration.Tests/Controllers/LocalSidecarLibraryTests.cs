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
