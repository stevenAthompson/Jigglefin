using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.LibraryStructureDto;
using Jellyfin.Api.Models.MediaInfoDtos;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Sdk;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class LiveDvdIsoTests
{
    [Fact]
    public async Task SelectedDvdIso_ProbesAndStreamsWithoutWritingToMedia()
    {
        var iso = Environment.GetEnvironmentVariable("JIGGLEFIN_TEST_DVD_ISO");
        var ffmpeg = Environment.GetEnvironmentVariable("JIGGLEFIN_TEST_FFMPEG");
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(iso) || string.IsNullOrEmpty(ffmpeg))
        {
            throw SkipException.ForSkip("Set JIGGLEFIN_TEST_DVD_ISO and JIGGLEFIN_TEST_FFMPEG for the read-only DVD ISO integration test on Windows.");
        }

        var info = new FileInfo(iso);
        Assert.True(info.Exists);
        Assert.Equal(".iso", info.Extension, ignoreCase: true);
        var length = info.Length;
        var lastWrite = info.LastWriteTimeUtc;
        var fixture = Directory.CreateTempSubdirectory("jigglefin-dvd-playback-");
        try
        {
            using var factory = new JellyfinApplicationFactory { FfmpegPath = ffmpeg, TestProfilePath = Path.Combine(fixture.FullName, "Profile") };
            using var client = factory.CreateClient();
            var token = await AuthHelper.CompleteStartupAsync(client);
            client.DefaultRequestHeaders.AddAuthHeader(token);
            using var create = await client.PostAsJsonAsync(
                "Library/VirtualFolders?name=DVD&refreshLibrary=true",
                new AddVirtualFolderDto { LibraryOptions = new LibraryOptions { PathInfos = [new MediaPathInfo(info.DirectoryName!)] } },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, create.StatusCode);

            var views = await Get<QueryResult<BaseItemDto>>(client, "UserViews");
            var libraryId = Assert.Single(views.Items).Id;
            var listing = await Get<QueryResult<BaseItemDto>>(client, $"Items?parentId={libraryId}");
            var selected = Assert.Single(listing.Items, item => item.Name.Equals(info.Name, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(MediaType.Video, selected.MediaType);
            var listedSource = Assert.Single(selected.MediaSources);
            Assert.Empty(listedSource.MediaStreams);
            Assert.Equal(VideoType.Iso, listedSource.VideoType);
            Assert.False(listedSource.SupportsDirectPlay);

            var playback = await Get<PlaybackInfoResponse>(client, $"Items/{selected.Id}/PlaybackInfo");
            var source = Assert.Single(playback.MediaSources);
            Assert.Equal(VideoType.Iso, source.VideoType);
            Assert.Equal(IsoType.Dvd, source.IsoType);
            Assert.False(source.SupportsDirectPlay);
            Assert.False(source.SupportsDirectStream);
            Assert.True(source.RunTimeTicks > 0);
            Assert.Contains(source.MediaStreams, stream => stream.Type == MediaStreamType.Video);

            using (var compatibleResponse = await client.PostAsJsonAsync(
                $"Items/{selected.Id}/PlaybackInfo",
                new PlaybackInfoDto
                {
                    EnableDirectPlay = true,
                    EnableTranscoding = true,
                    DeviceProfile = new DeviceProfile
                    {
                        DirectPlayProfiles = [new DirectPlayProfile { Type = DlnaProfileType.Video, Container = "iso,mpeg", VideoCodec = "mpeg2video" }],
                        TranscodingProfiles = [new TranscodingProfile { Type = DlnaProfileType.Video, Container = "ts", Protocol = MediaStreamProtocol.hls, VideoCodec = "h264", AudioCodec = "aac", Context = EncodingContext.Streaming }]
                    }
                },
                JsonDefaults.Options,
                TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.OK, compatibleResponse.StatusCode);
                var compatible = await compatibleResponse.Content.ReadFromJsonAsync<PlaybackInfoResponse>(JsonDefaults.Options, TestContext.Current.CancellationToken);
                var compatibleSource = Assert.Single(compatible!.MediaSources);
                Assert.False(compatibleSource.SupportsDirectPlay);
                Assert.Contains(".m3u8", compatibleSource.TranscodingUrl, StringComparison.Ordinal);
            }

            var session = Guid.NewGuid().ToString("N");
            var playlistPath = $"Videos/{selected.Id}/live.m3u8?videoCodec=h264&audioCodec=aac&allowVideoStreamCopy=false&allowAudioStreamCopy=false&segmentContainer=ts&segmentLength=1&minSegments=1&playSessionId={session}&deviceId=dvd-test";
            try
            {
                using var playlistResponse = await client.GetAsync(playlistPath, TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.OK, playlistResponse.StatusCode);
                var playlist = await playlistResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                Assert.Contains("#EXTM3U", playlist, StringComparison.Ordinal);
                var segmentLine = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).First(line => !line.StartsWith('#'));
                using var segmentResponse = await client.GetAsync(new Uri(new Uri(client.BaseAddress!, playlistPath), segmentLine), TestContext.Current.CancellationToken);
                if (segmentResponse.StatusCode != HttpStatusCode.OK)
                {
                    var logs = Directory.GetFiles(Path.Combine(fixture.FullName, "Profile", "logs"), "FFmpeg*.log");
                    Assert.Fail($"Segment returned {segmentResponse.StatusCode}: {await segmentResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}\n{string.Join("\n", logs.Select(File.ReadAllText))}");
                }

                var segment = await segmentResponse.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
                Assert.True(segment.Length >= 188);
                Assert.Equal(0x47, segment[0]);
            }
            finally
            {
                using var stop = await client.DeleteAsync($"Videos/ActiveEncodings?deviceId=dvd-test&playSessionId={session}", TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
            }

            Assert.Equal(length, info.Length);
            Assert.Equal(lastWrite, info.LastWriteTimeUtc);
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
        return await response.Content.ReadFromJsonAsync<T>(JsonDefaults.Options, TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("Missing API response.");
    }
}
