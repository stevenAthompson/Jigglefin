using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Threading.Tasks;
using Jellyfin.Api.Models.LibraryStructureDto;
using Jellyfin.Api.Models.MediaInfoDtos;
using Jellyfin.Database.Implementations;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Sdk;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class LivePlaybackTests
{
    [Theory]
    [InlineData("m4b", "Audio")]
    [InlineData("mp4", "Videos")]
    public async Task SelectedFile_PlaysAndRetainsResumeAfterCacheEvictionAndServerRestart(string extension, string streamRoute)
    {
        var ffmpeg = Environment.GetEnvironmentVariable("JIGGLEFIN_TEST_FFMPEG");
        if (string.IsNullOrEmpty(ffmpeg))
        {
            throw SkipException.ForSkip("Set JIGGLEFIN_TEST_FFMPEG for real probe/stream/transcode tests.");
        }

        var fixture = Directory.CreateTempSubdirectory("jigglefin-live-playback-");
        try
        {
            var mediaRoot = Directory.CreateDirectory(Path.Combine(fixture.FullName, "Media"));
            var profile = Path.Combine(fixture.FullName, "Profile");
            var source = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Test Data", "JigglefinSample." + extension), TestContext.Current.CancellationToken);
            var media = Path.Combine(mediaRoot.FullName, "Chapter." + extension);
            var nfo = Path.Combine(mediaRoot.FullName, "Chapter.nfo");
            await File.WriteAllBytesAsync(media, source, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(nfo, "<movie><title>Selected title</title><plot>Local description</plot><resume><position>9999</position></resume><trailer>https://example.invalid/never-fetch</trailer></movie>", TestContext.Current.CancellationToken);
            var artwork = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9ioAAAAASUVORK5CYII=");
            var poster = Path.Combine(mediaRoot.FullName, "folder.png");
            await File.WriteAllBytesAsync(poster, artwork, TestContext.Current.CancellationToken);
            var subtitlePath = Path.Combine(mediaRoot.FullName, "Chapter.en.srt");
            const string subtitleText = "1\n00:00:00,000 --> 00:00:01,000\nLocal subtitle\n";
            if (extension == "mp4")
            {
                await File.WriteAllTextAsync(subtitlePath, subtitleText, TestContext.Current.CancellationToken);
            }

            var initialTime = File.GetLastWriteTimeUtc(media);
            Guid itemId;
            Guid libraryId;
            string token;
            var bookmark = TimeSpan.FromMilliseconds(650).Ticks;

            using (var factory = new JellyfinApplicationFactory { FfmpegPath = ffmpeg, TestProfilePath = profile })
            using (var client = factory.CreateClient())
            {
                token = await AuthHelper.CompleteStartupAsync(client);
                client.DefaultRequestHeaders.AddAuthHeader(token);
                var databaseFactory = factory.Services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
                await using var database = await databaseFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
                var initialCount = await database.BaseItems.CountAsync(TestContext.Current.CancellationToken);
                using var create = await client.PostAsJsonAsync(
                    "Library/VirtualFolders?name=Media&refreshLibrary=true",
                    new AddVirtualFolderDto { LibraryOptions = new LibraryOptions { PathInfos = [new MediaPathInfo(mediaRoot.FullName)] } },
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, create.StatusCode);
                var views = await Get<QueryResult<BaseItemDto>>(client, "UserViews");
                libraryId = Assert.Single(views.Items).Id;
                var listing = await Get<QueryResult<BaseItemDto>>(client, $"Items?parentId={libraryId}");
                var file = Assert.Single(listing.Items, entry => entry.Name == "Chapter." + extension);
                itemId = file.Id;
                Assert.Null(file.RunTimeTicks);
                Assert.Empty(file.ImageTags);
                var details = await Get<BaseItemDto>(client, $"Items/{itemId}");
                Assert.Equal("Selected title", details.Name);
                Assert.Equal("Local description", details.Overview);
                Assert.Equal(0, details.UserData.PlaybackPositionTicks);
                Assert.True(details.ImageTags.ContainsKey(ImageType.Primary));
                using var image = await client.GetAsync($"Items/{itemId}/Images/Primary?maxWidth=64", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, image.StatusCode);
                Assert.StartsWith("image/", image.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);

                var playback = await Get<PlaybackInfoResponse>(client, $"Items/{itemId}/PlaybackInfo");
                var mediaSource = Assert.Single(playback.MediaSources);
                Assert.True(mediaSource.RunTimeTicks > bookmark);
                Assert.NotEmpty(mediaSource.MediaStreams);
                Assert.Equal(MediaProtocol.File, mediaSource.Protocol);
                if (extension == "mp4")
                {
                    var subtitle = Assert.Single(mediaSource.MediaStreams, stream => stream.Type == MediaStreamType.Subtitle && stream.IsExternal);
                    using var text = await client.GetAsync($"Videos/{itemId}/{mediaSource.Id}/Subtitles/{subtitle.Index}/Stream.vtt", TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.OK, text.StatusCode);
                    var vtt = await text.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                    Assert.Contains("WEBVTT", vtt, StringComparison.Ordinal);
                    Assert.Contains("Local subtitle", vtt, StringComparison.Ordinal);
                }

                using var direct = await client.GetAsync($"{streamRoute}/{itemId}/stream?static=true", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
                Assert.Equal(source, await direct.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
                using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"{streamRoute}/{itemId}/stream?static=true");
                rangeRequest.Headers.Range = new RangeHeaderValue(100, 199);
                using var range = await client.SendAsync(rangeRequest, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
                Assert.Equal(source[100..200], await range.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

                if (extension == "m4b")
                {
                    using var transcode = await client.GetAsync($"Audio/{itemId}/stream.mp3?audioCodec=mp3&allowAudioStreamCopy=false&audioBitRate=64000", TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.OK, transcode.StatusCode);
                    var mp3 = await transcode.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
                    Assert.True(mp3.Length > 128);
                    Assert.Equal("ID3", System.Text.Encoding.ASCII.GetString(mp3, 0, 3));
                }
                else
                {
                    var subtitleIndex = Assert.Single(mediaSource.MediaStreams, stream => stream.IsExternal && stream.Type == MediaStreamType.Subtitle).Index;
                    var playlistPath = $"Videos/{itemId}/main.m3u8?videoCodec=h264&audioCodec=aac&allowVideoStreamCopy=false&allowAudioStreamCopy=false&segmentContainer=ts&segmentLength=1&subtitleMethod=Encode&subtitleStreamIndex={subtitleIndex}";
                    using var playlistResponse = await client.GetAsync(playlistPath, TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.OK, playlistResponse.StatusCode);
                    var playlist = await playlistResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                    Assert.Contains("#EXTM3U", playlist, StringComparison.Ordinal);
                    var segmentLine = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).First(line => !line.StartsWith('#'));
                    using var segmentResponse = await client.GetAsync(new Uri(new Uri(client.BaseAddress!, playlistPath), segmentLine), TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.OK, segmentResponse.StatusCode);
                    var segment = await segmentResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
                    Assert.True(segment.Length >= 188);
                    Assert.Equal(0x47, segment[0]);
                }

                using var start = await client.PostAsJsonAsync(
                    "Sessions/Playing",
                    new PlaybackStartInfo
                    {
                        ItemId = itemId,
                        MediaSourceId = mediaSource.Id,
                        PlaySessionId = playback.PlaySessionId,
                        CanSeek = true,
                        PositionTicks = 0,
                        PlayMethod = PlayMethod.DirectPlay
                    },
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, start.StatusCode);
                using var stopped = await client.PostAsJsonAsync(
                    "Sessions/Playing/Stopped",
                    new PlaybackStopInfo
                    {
                        ItemId = itemId,
                        MediaSourceId = mediaSource.Id,
                        PlaySessionId = playback.PlaySessionId,
                        PositionTicks = bookmark
                    },
                    JsonDefaults.Options,
                    TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
                factory.Services.GetRequiredService<ILiveItemService>().ClearCache();
                Assert.Equal(bookmark, (await Get<BaseItemDto>(client, $"Items/{itemId}")).UserData.PlaybackPositionTicks);
                var resume = await Get<QueryResult<BaseItemDto>>(client, "UserItems/Resume");
                Assert.Equal(itemId, Assert.Single(resume.Items).Id);
                Assert.Equal(initialCount, await database.BaseItems.CountAsync(TestContext.Current.CancellationToken));

                if (extension == "mp4")
                {
                    // A renamed HLS/concat file must not connect or resolve its referenced
                    // inputs. The local trap would observe even an attempted TCP connection.
                    var trap = new TcpListener(IPAddress.Loopback, 0);
                    trap.Start();
                    try
                    {
                        var port = ((IPEndPoint)trap.LocalEndpoint).Port;
                        var outside = Path.Combine(fixture.FullName, "Outside.mp4");
                        await File.WriteAllBytesAsync(outside, source, TestContext.Current.CancellationToken);
                        var attacks = new[]
                        {
                            $"#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\nhttp://127.0.0.1:{port}/never.ts\n#EXT-X-ENDLIST\n",
                            "ffconcat version 1.0\nfile '" + outside.Replace('\\', '/') + "'\n"
                        };
                        for (var index = 0; index < attacks.Length; index++)
                        {
                            var name = "Disguised" + index + ".mp4";
                            await File.WriteAllTextAsync(Path.Combine(mediaRoot.FullName, name), attacks[index], TestContext.Current.CancellationToken);
                            var fresh = await Get<QueryResult<BaseItemDto>>(client, $"Items?parentId={libraryId}");
                            var attackId = Assert.Single(fresh.Items, entry => entry.Name == name).Id;
                            var rejected = await Get<PlaybackInfoResponse>(client, $"Items/{attackId}/PlaybackInfo");
                            Assert.Empty(rejected.MediaSources);
                            Assert.Equal(PlaybackErrorCode.NoCompatibleStream, rejected.ErrorCode);
                        }

                        // The burn-in filter must not autodetect a playlist disguised as
                        // a text subtitle. Managed parsing rejects it before native use.
                        var subtitle = Assert.Single(mediaSource.MediaStreams, stream => stream.IsExternal && stream.Type == MediaStreamType.Subtitle);
                        await File.WriteAllTextAsync(subtitlePath, attacks[0], TestContext.Current.CancellationToken);
                        var subtitleEncoder = factory.Services.GetRequiredService<ISubtitleEncoder>();
                        await Assert.ThrowsAsync<ArgumentException>(() => subtitleEncoder.GetSubtitleFilePath(subtitle, mediaSource, TestContext.Current.CancellationToken));
                        await File.WriteAllTextAsync(subtitlePath, subtitleText, TestContext.Current.CancellationToken);
                        var normalized = await subtitleEncoder.GetSubtitleFilePath(subtitle, mediaSource, TestContext.Current.CancellationToken);
                        Assert.DoesNotContain(mediaRoot.FullName, normalized, StringComparison.OrdinalIgnoreCase);
                        Assert.EndsWith(".ass", normalized, StringComparison.Ordinal);
                        Assert.Contains("Local subtitle", await File.ReadAllTextAsync(normalized, TestContext.Current.CancellationToken), StringComparison.Ordinal);
                        Assert.False(trap.Pending());
                    }
                    finally
                    {
                        trap.Stop();
                    }
                }
            }

            using (var restarted = new JellyfinApplicationFactory { FfmpegPath = ffmpeg, TestProfilePath = profile })
            using (var client = restarted.CreateClient())
            {
                client.DefaultRequestHeaders.AddAuthHeader(token);
                var details = await Get<BaseItemDto>(client, $"Items/{itemId}");
                Assert.Equal(bookmark, details.UserData.PlaybackPositionTicks);
                Assert.True(details.RunTimeTicks > bookmark);
                Assert.True(details.UserData.PlayedPercentage > 0);
                var resume = await Get<QueryResult<BaseItemDto>>(client, "UserItems/Resume");
                Assert.Equal(bookmark, Assert.Single(resume.Items).UserData.PlaybackPositionTicks);
                var listing = await Get<QueryResult<BaseItemDto>>(client, $"Items?parentId={libraryId}");
                Assert.Equal(bookmark, Assert.Single(listing.Items, entry => entry.Id.Equals(itemId)).UserData.PlaybackPositionTicks);
                Assert.Equal(source, await File.ReadAllBytesAsync(media, TestContext.Current.CancellationToken));
                Assert.Equal(initialTime, File.GetLastWriteTimeUtc(media));
                Assert.Equal(artwork, await File.ReadAllBytesAsync(poster, TestContext.Current.CancellationToken));
                if (extension == "mp4")
                {
                    Assert.Equal(subtitleText, await File.ReadAllTextAsync(subtitlePath, TestContext.Current.CancellationToken));
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            fixture.Delete(true);
        }
    }

    private static async Task<T> Get<T>(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, url + ": " + await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options, TestContext.Current.CancellationToken) ?? throw new InvalidOperationException("Missing API response.");
    }
}
