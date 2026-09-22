using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

public sealed class FolderFirstStreamingTests
{
    [Fact]
    public async Task MusicFile_BrowsesAndStreamsThroughStandardClientEndpoints()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-stream-" + Guid.NewGuid().ToString("N"));
        var albumFolder = Path.Combine(testRoot, "Action", "Example Album");
        Directory.CreateDirectory(albumFolder);
        var audioBytes = CreateSilentWave();
        await File.WriteAllBytesAsync(Path.Combine(albumFolder, "Test Track.wav"), audioBytes, TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin stream test " + Guid.NewGuid().ToString("N");
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
                "UserViews?presetViews=music",
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

            var albums = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(albums);
            var album = Assert.Single(albums.Items, item => item.Name == "Example Album");
            Assert.Equal(BaseItemKind.MusicAlbum, album.Type);

            var tracks = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={album.Id}",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(tracks);
            var track = Assert.Single(tracks.Items, item => item.Name.Contains("Test Track", StringComparison.Ordinal));
            Assert.Equal(BaseItemKind.Audio, track.Type);

            using var streamResponse = await client.GetAsync(
                $"Audio/{track.Id}/stream?static=true",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            Assert.Equal(audioBytes, await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"Audio/{track.Id}/stream?static=true");
            rangeRequest.Headers.Range = new RangeHeaderValue(100, 199);
            using var rangeResponse = await client.SendAsync(rangeRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
            Assert.Equal(audioBytes[100..200], await rangeResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
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
