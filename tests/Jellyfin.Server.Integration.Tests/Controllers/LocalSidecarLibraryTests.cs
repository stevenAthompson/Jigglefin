using System;
using System.IO;
using System.Net;
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
    public async Task MovieNfo_ProvidesClientMetadataWithoutHidingPhysicalGroups()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-sidecar-" + Guid.NewGuid().ToString("N"));
        var movieFolder = Path.Combine(testRoot, "Action", "Example Movie (2020)");
        Directory.CreateDirectory(movieFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(movieFolder, "Example Movie (2020).mkv"),
            [],
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
