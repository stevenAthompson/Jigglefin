using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

public sealed class FolderFirstLibraryTests
{
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
            (CollectionType: "music", ItemFolder: "Example Album", FileName: "Track 01.mp3", ExpectedKind: BaseItemKind.MusicAlbum)
        };
        foreach (var library in libraries)
        {
            var mediaFolder = Path.Combine(testRoot, library.ExpectedKind.ToString(), "Action", library.ItemFolder);
            Directory.CreateDirectory(mediaFolder);
            await File.WriteAllBytesAsync(Path.Combine(mediaFolder, library.FileName), [], TestContext.Current.CancellationToken);
        }

        var looseEpisodeFolder = Path.Combine(testRoot, nameof(BaseItemKind.Series), "Drama");
        Directory.CreateDirectory(looseEpisodeFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(looseEpisodeFolder, "Another Show - S01E01.mkv"),
            [],
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
                Assert.True(
                    secondLevel.Items.Any(item => item.Name.Contains(library.ItemFolder.Split('(')[0].Trim(), StringComparison.Ordinal)
                        && item.Type == library.ExpectedKind),
                    $"No {library.ExpectedKind} for {library.CollectionType}; second level: {string.Join(", ", secondLevel.Items.Select(item => item.Name + ":" + item.Type))}");
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
