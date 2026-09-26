using System;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.LibraryStructureDto;
using Jellyfin.Extensions.Json;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class LocalFirstLibraryOptionsTests
{
    [Fact]
    public async Task CreateLibrary_RemoteAndMediaWritingOptionsCannotBeOptedInto()
    {
        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));

        var name = "jigglefin-local-first-" + Guid.NewGuid().ToString("N");
        var explicitName = name + "-explicit";
        var explicitOptions = new TypeOptions
        {
            Type = "Movie",
            MetadataFetchers = ["Chosen Remote Metadata"],
            ImageFetchers = ["Chosen Remote Image"]
        };

        try
        {
            foreach (var (libraryName, options) in new[]
            {
                (name, new LibraryOptions()),
                (explicitName, new LibraryOptions
                {
                    TypeOptions = [explicitOptions], SaveLocalMetadata = true, SaveSubtitlesWithMedia = true,
                    SaveLyricsWithMedia = true, SaveTrickplayWithMedia = true, EnableRealtimeMonitor = true,
                    EnableChapterImageExtraction = true, EnableTrickplayImageExtraction = true,
                    MetadataSavers = ["Nfo"], SubtitleDownloadLanguages = ["eng"]
                })
            })
            {
                using var createResponse = await client.PostAsJsonAsync(
                    $"Library/VirtualFolders?name={libraryName}&collectionType=movies&refreshLibrary=false",
                    new AddVirtualFolderDto { LibraryOptions = options },
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);
            }

            var libraries = await client.GetFromJsonAsync<VirtualFolderInfo[]>(
                "Library/VirtualFolders",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(libraries);

            foreach (var libraryName in new[] { name, explicitName })
            {
                var library = Assert.Single(libraries, library => library.Name == libraryName);
                var options = library.LibraryOptions;
                Assert.NotNull(options);
                Assert.Empty(options.TypeOptions);
                Assert.Empty(options.MetadataSavers!);
                Assert.False(options.SaveLocalMetadata);
                Assert.False(options.SaveSubtitlesWithMedia);
                Assert.False(options.SaveLyricsWithMedia);
                Assert.False(options.SaveTrickplayWithMedia);
                Assert.False(options.EnableRealtimeMonitor);
                Assert.False(options.EnableChapterImageExtraction);
                Assert.False(options.EnableTrickplayImageExtraction);
                Assert.Null(library.CollectionType);
            }
        }
        finally
        {
            foreach (var libraryName in new[] { name, explicitName })
            {
                using var deleteResponse = await client.DeleteAsync(
                    $"Library/VirtualFolders?name={libraryName}&refreshLibrary=false",
                    TestContext.Current.CancellationToken);
                Assert.True(
                    deleteResponse.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound,
                    $"Unexpected delete status for {libraryName}: {deleteResponse.StatusCode}");
            }
        }
    }
}
