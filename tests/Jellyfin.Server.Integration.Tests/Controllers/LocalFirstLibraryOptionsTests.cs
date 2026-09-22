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
    public async Task CreateLibrary_UsesLocalFirstDefaultsUnlessTypeOptionsAreExplicit()
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
                (explicitName, new LibraryOptions { TypeOptions = [explicitOptions] })
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

            var defaultLibrary = Assert.Single(libraries, library => library.Name == name);
            var defaultType = Assert.Single(defaultLibrary.LibraryOptions!.TypeOptions);
            Assert.Equal("Movie", defaultType.Type);
            Assert.Empty(defaultType.MetadataFetchers);
            Assert.Equal(["Screen Grabber", "Image Extractor"], defaultType.ImageFetchers);

            var configuredLibrary = Assert.Single(libraries, library => library.Name == explicitName);
            var configuredType = Assert.Single(configuredLibrary.LibraryOptions!.TypeOptions);
            Assert.Equal(explicitOptions.MetadataFetchers, configuredType.MetadataFetchers);
            Assert.Equal(explicitOptions.ImageFetchers, configuredType.ImageFetchers);
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
