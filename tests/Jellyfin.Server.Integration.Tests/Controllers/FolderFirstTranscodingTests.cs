using System;
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
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class FolderFirstTranscodingTests
{
    [Fact]
    public async Task AudioBook_TranscodesToMp3WithBundledFfmpeg()
    {
        var ffmpegPath = Environment.GetEnvironmentVariable("JIGGLEFIN_TEST_FFMPEG");
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            throw SkipException.ForSkip("Set JIGGLEFIN_TEST_FFMPEG to run the real FFmpeg transcode test.");
        }

        Assert.True(File.Exists(ffmpegPath), $"FFmpeg does not exist: {ffmpegPath}");
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-audiobook-transcode-" + Guid.NewGuid().ToString("N"));
        var audiobookFolder = Path.Combine(testRoot, "Action", "Example Audio Book");
        Directory.CreateDirectory(audiobookFolder);
        var sourceBytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.m4b"),
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            Path.Combine(audiobookFolder, "Example Audio Book.m4b"),
            sourceBytes,
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory { FfmpegPath = ffmpegPath };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin audiobook transcode test " + Guid.NewGuid().ToString("N");
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
            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var action = Assert.Single(groups.Items, item => item.Name == "Action");
            Assert.Equal(BaseItemKind.Folder, action.Type);
            var books = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(books);
            var audiobook = Assert.Single(books.Items);
            Assert.Equal(BaseItemKind.AudioBook, audiobook.Type);

            using var streamResponse = await client.GetAsync(
                $"Audio/{audiobook.Id}/stream.mp3?audioCodec=mp3&allowAudioStreamCopy=false&audioBitRate=64000",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
            var mp3 = await streamResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            Assert.True(mp3.Length > 128, "The transcoded MP3 was empty.");
            Assert.Equal("ID3", System.Text.Encoding.ASCII.GetString(mp3, 0, 3));
            Assert.NotEqual(sourceBytes, mp3);
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
    public async Task Movie_TranscodesToHlsWithBundledFfmpeg()
    {
        var ffmpegPath = Environment.GetEnvironmentVariable("JIGGLEFIN_TEST_FFMPEG");
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            throw SkipException.ForSkip("Set JIGGLEFIN_TEST_FFMPEG to run the real FFmpeg transcode test.");
        }

        Assert.True(File.Exists(ffmpegPath), $"FFmpeg does not exist: {ffmpegPath}");
        var testRoot = Path.Combine(Path.GetTempPath(), "jigglefin-hls-test-" + Guid.NewGuid().ToString("N"));
        var movieFolder = Path.Combine(testRoot, "Action", "Transcode Movie");
        Directory.CreateDirectory(movieFolder);
        await File.WriteAllBytesAsync(
            Path.Combine(movieFolder, "Transcode Movie.mp4"),
            await File.ReadAllBytesAsync(
                Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample.mp4"),
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(movieFolder, "Transcode Movie.eng.srt"),
            "1\n00:00:00,000 --> 00:00:01,000\nA local subtitle.\n",
            TestContext.Current.CancellationToken);

        using var factory = new JellyfinApplicationFactory { FfmpegPath = ffmpegPath };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var libraryName = "Jigglefin HLS test " + Guid.NewGuid().ToString("N");
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
            var groups = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={library.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(groups);
            var action = Assert.Single(groups.Items, item => item.Name == "Action");
            Assert.Equal(BaseItemKind.Folder, action.Type);
            var movies = await client.GetFromJsonAsync<QueryResult<BaseItemDto>>(
                $"Items?parentId={action.Id}", JsonDefaults.Options, TestContext.Current.CancellationToken);
            Assert.NotNull(movies);
            var movie = Assert.Single(movies.Items);
            Assert.Equal(BaseItemKind.Movie, movie.Type);

            var movieDetails = await client.GetFromJsonAsync<BaseItemDto>(
                $"Items/{movie.Id}?fields=MediaSources,MediaStreams",
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.NotNull(movieDetails);
            var mediaSource = Assert.Single(movieDetails.MediaSources);
            var subtitle = Assert.Single(mediaSource.MediaStreams, stream => stream.Type == MediaStreamType.Subtitle && stream.IsExternal);
            Assert.Equal("eng", subtitle.Language);
            using var subtitleResponse = await client.GetAsync(
                $"Videos/{movie.Id}/{Uri.EscapeDataString(mediaSource.Id)}/Subtitles/{subtitle.Index}/Stream.srt",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, subtitleResponse.StatusCode);
            Assert.Contains("A local subtitle.", await subtitleResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

            var playlistPath = $"Videos/{movie.Id}/main.m3u8?videoCodec=h264&audioCodec=aac&allowVideoStreamCopy=false&allowAudioStreamCopy=false&segmentContainer=ts&segmentLength=1";
            using var playlistResponse = await client.GetAsync(playlistPath, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, playlistResponse.StatusCode);
            var playlist = await playlistResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("#EXTM3U", playlist, StringComparison.Ordinal);
            var segmentLine = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .First(line => !line.StartsWith('#'));
            var segmentUri = new Uri(new Uri(client.BaseAddress!, playlistPath), segmentLine);
            using var segmentResponse = await client.GetAsync(segmentUri, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, segmentResponse.StatusCode);
            var segment = await segmentResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            Assert.True(segment.Length >= 188, "The HLS media segment was empty.");
            Assert.Equal(0x47, segment[0]);
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
